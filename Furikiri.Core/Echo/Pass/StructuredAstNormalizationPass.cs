using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 对已经完成区域恢复的 AST 做有控制流证明的规范化。
    /// </summary>
    /// <remarks>
    /// 本阶段不再读取 CFG，也不隐藏基本块。它只处理 AST 本身能够证明等价的形态，
    /// 使语言输出器保持为纯格式化阶段。
    /// </remarks>
    internal sealed class StructuredAstNormalizationPass : IPass
    {
        public BlockStatement Process(
            DecompileContext context, BlockStatement statement)
        {
            BooleanPhiNormalizer.Normalize(statement);
            NormalizeTerminalOuterGuard(statement);
            NormalizeBlock(statement);
            return statement;
        }

        private static void NormalizeBlock(
            BlockStatement block, bool isLoopBody = false)
        {
            if (block?.Statements == null)
            {
                return;
            }

            // 公共返回块也可能位于嵌套正文中，因此每个块都要参与规范化。
            NormalizeDuplicatedTerminalReturn(block);
            ConsumedConditionStatementNormalizer.Normalize(block);
            StructuredConditionalNormalizer.NormalizeAdjacentConditions(block);
            GuardedAssignmentNormalizer.Normalize(block);

            for (var index = 0; index < block.Statements.Count; index++)
            {
                if (block.Statements[index] is IfStatement candidate &&
                    EqualitySwitchNormalizer.TryNormalizeWithContinuation(
                        candidate,
                        isLoopBody && index == block.Statements.Count - 1,
                        out var normalization))
                {
                    block.Statements[index] = normalization.Switch;
                    if (normalization.Continuation != null)
                    {
                        block.Statements.InsertRange(
                            index + 1,
                            ExpandStatement(normalization.Continuation).ToList());
                    }
                }
            }

            for (var index = 0; index < block.Statements.Count; index++)
            {
                NormalizeNode(block.Statements[index]);
                if (block.Statements[index] is not IfStatement conditional ||
                    conditional.Else == null ||
                    !StructuredControlFlowFacts.AlwaysTerminates(
                        conditional.Then))
                {
                    continue;
                }

                // `if (C) return/throw; else body;` 中，C 为真时已经终止当前路径，
                // 因此 else 正文可安全移到 if 之后。该变换不反转条件、不复制求值，
                // 并让顺序早退保持为源码级 Sequence，而不是嵌套分支。
                var following = ExpandStatement(conditional.Else).ToList();
                conditional.Else = null;
                foreach (var nestedIf in following.OfType<IfStatement>())
                {
                    nestedIf.IsElseIf = false;
                }
                block.Statements.InsertRange(index + 1, following);
            }

            StructuredConditionalNormalizer
                .NormalizeSharedFollowingLoopTransfers(block);
        }

        /// <summary>
        /// 函数末尾常被恢复成 <c>if (valid) { if (reject) return; body; }</c>。
        /// 外层条件为假时本来就是隐式返回，可等价改写成顺序早退，减少无意义嵌套。
        /// </summary>
        private static void NormalizeTerminalOuterGuard(BlockStatement block)
        {
            if (block?.Statements?.Count != 1 ||
                block.Statements[0] is not IfStatement outer ||
                outer.Else != null ||
                outer.Then is not BlockStatement outerBody ||
                outerBody.Statements.Count < 2 ||
                outerBody.Statements[0] is not IfStatement inner ||
                inner.Else != null ||
                inner.Then is not BlockStatement returnBody ||
                returnBody.Statements.Count != 1 ||
                !IsVoidReturnNode(returnBody.Statements[0]))
            {
                return;
            }

            var guard = new IfStatement(
                outer.Condition.Invert().Or(inner.Condition), inner.Then, null);
            block.Statements = new List<IAstNode> { guard };
            block.Statements.AddRange(outerBody.Statements.Skip(1));
        }

        private static bool IsVoidReturnNode(IAstNode node)
        {
            return node is ReturnExpression { Return: null } ||
                   node is ExpressionStatement
                   {
                       Expression: ReturnExpression { Return: null }
                   };
        }

        /// <summary>
        /// 带副作用的公共返回值不能复制到提前返回分支。将
        /// <c>if (skip) return value; body; return value;</c> 改写为
        /// <c>if (!skip) body; return value;</c>，确保返回表达式只求值一次。
        /// </summary>
        private static void NormalizeDuplicatedTerminalReturn(BlockStatement block)
        {
            if (block?.Statements == null || block.Statements.Count < 3 ||
                TryGetReturnExpression(block.Statements[^1]) is not { } finalReturn ||
                finalReturn.Return == null ||
                ExpressionEffectAnalysis.IsProvablySideEffectFree(finalReturn.Return))
            {
                return;
            }

            for (var index = block.Statements.Count - 2; index >= 0; index--)
            {
                if (block.Statements[index] is not IfStatement guard ||
                    guard.IsElseIf || guard.Else != null ||
                    TryGetSingleReturnBody(guard.Then) is not { } guardedReturn ||
                    guardedReturn.Return == null ||
                    !AreSameEffectfulEvaluation(
                        guardedReturn.Return, finalReturn.Return))
                {
                    continue;
                }

                var middleCount = block.Statements.Count - index - 2;
                var sharedBody = new BlockStatement
                {
                    Statements = block.Statements
                        .Skip(index + 1)
                        .Take(middleCount)
                        .ToList()
                };
                if (sharedBody.Statements.Count == 0)
                {
                    block.Statements.RemoveAt(index);
                    return;
                }

                guard.Condition = guard.Condition.Invert();
                guard.Then = sharedBody;
                block.Statements.RemoveRange(index + 1, middleCount);
                return;
            }
        }

        /// <summary>
        /// 结构相同不代表同一次带副作用求值。只有 AST 仍共享节点，或两处都
        /// 指向同一条 VM 定义指令时，才可把提前返回与末尾公共返回合并。
        /// </summary>
        private static bool AreSameEffectfulEvaluation(
            Expression left, Expression right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            return left?.CachedEvaluationId.HasValue == true &&
                   right?.CachedEvaluationId.HasValue == true &&
                   left.CachedEvaluationId == right.CachedEvaluationId;
        }

        private static ReturnExpression TryGetSingleReturnBody(Statement body)
        {
            if (body is BlockStatement { Statements.Count: 1 } block)
            {
                return TryGetReturnExpression(block.Statements[0]);
            }

            return TryGetReturnExpression(body);
        }

        private static ReturnExpression TryGetReturnExpression(IAstNode node)
        {
            return node switch
            {
                ReturnExpression direct => direct,
                ExpressionStatement { Expression: ReturnExpression wrapped } => wrapped,
                _ => null
            };
        }

        private static IEnumerable<IAstNode> ExpandStatement(Statement statement)
        {
            return statement is BlockStatement block
                ? block.Statements
                : new IAstNode[] { statement };
        }

        private static void NormalizeNode(IAstNode node)
        {
            switch (node)
            {
                case BlockStatement block:
                    NormalizeBlock(block);
                    break;
                case IfStatement conditional:
                    StructuredConditionalNormalizer.NormalizeConditional(conditional);
                    conditional.Then = NormalizeNestedStatement(conditional.Then);
                    conditional.Else = NormalizeNestedStatement(conditional.Else);
                    break;
                case WhileStatement loop:
                    NormalizeBlock(loop.Body, true);
                    break;
                case DoWhileStatement loop:
                    NormalizeBlock(loop.Body, true);
                    break;
                case ForStatement loop:
                    NormalizeBlock(loop.Body, true);
                    break;
                case SwitchStatement switchStatement:
                    foreach (var @case in switchStatement.Cases)
                    {
                        NormalizeBlock(@case.Body);
                    }
                    NormalizeBlock(switchStatement.Default);
                    break;
                case TryStatement tryStatement:
                    NormalizeBlock(tryStatement.Try);
                    // 当前 AST 暂以 Finally 字段承载 catch 正文。
                    NormalizeBlock(tryStatement.Finally);
                    break;
            }
        }

        /// <summary>
        /// equality 分派不一定直接位于语句块列表中，也可能是普通条件的 then/else
        /// 子节点。父引用必须在这里显式替换，否则递归虽能访问其各臂，却永远没有
        /// 机会把该 IfStatement 本身换成 SwitchStatement。
        /// </summary>
        private static Statement NormalizeNestedStatement(Statement statement)
        {
            if (statement is IfStatement candidate &&
                EqualitySwitchNormalizer.TryNormalizeWithContinuation(
                    candidate, false, out var normalization))
            {
                if (normalization.Continuation == null)
                {
                    NormalizeNode(normalization.Switch);
                    return normalization.Switch;
                }

                var replacement = new BlockStatement();
                replacement.Statements.Add(normalization.Switch);
                replacement.Statements.AddRange(
                    ExpandStatement(normalization.Continuation));
                NormalizeBlock(replacement);
                return replacement;
            }

            NormalizeNode(statement);
            return statement;
        }

    }
}
