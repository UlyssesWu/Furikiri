using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 对结构化后的相邻条件和嵌套条件做局部等价规范化。
    /// </summary>
    /// <remarks>
    /// 这里只读取 AST，不依赖基本块地址、Hidden 状态或输出文本。所有变换都在
    /// 语言写出前完成，避免同一条件因访问器调用顺序不同而得到不同结果。
    /// </remarks>
    internal static class StructuredConditionalNormalizer
    {
        /// <summary>
        /// 合并必须按块内顺序观察的相邻条件。采用一个候选后继续检查当前位置，
        /// 使三个连续的同类循环转移也能收敛成一个短路条件。
        /// </summary>
        public static void NormalizeAdjacentConditions(BlockStatement block)
        {
            if (block?.Statements == null || block.Statements.Count < 2)
            {
                return;
            }

            var index = 0;
            while (index + 1 < block.Statements.Count)
            {
                if (TryMergeGuardSelectorWithFollowingIf(
                        block.Statements[index], block.Statements[index + 1],
                        out var guardedIf) ||
                    TryMergeAdjacentLoopTransferIf(
                        block.Statements[index], block.Statements[index + 1],
                        out guardedIf))
                {
                    block.Statements[index] = guardedIf;
                    block.Statements.RemoveAt(index + 1);
                    continue;
                }

                index++;
            }
        }

        /// <summary>
        /// 删除条件臂末尾与紧随其后的公共循环转移重复的语句。
        /// </summary>
        /// <remarks>
        /// 例如 <c>if (C) { work(); continue; } continue;</c> 可安全化为
        /// <c>if (C) work(); continue;</c>。公共转移仍覆盖条件的两条路径，且不会
        /// 复制或重排条件、正文中的任何求值。
        /// </remarks>
        public static void NormalizeSharedFollowingLoopTransfers(BlockStatement block)
        {
            if (block?.Statements == null || block.Statements.Count < 2)
            {
                return;
            }

            for (var index = 0; index + 1 < block.Statements.Count; index++)
            {
                if (block.Statements[index] is not IfStatement conditional ||
                    block.Statements[index + 1] is not Statement commonTransfer ||
                    commonTransfer is not BreakStatement &&
                    commonTransfer is not ContinueStatement)
                {
                    continue;
                }

                conditional.Then = RemoveMatchingTrailingTransfer(
                    conditional.Then, commonTransfer.GetType());
                conditional.Else = RemoveMatchingTrailingTransfer(
                    conditional.Else, commonTransfer.GetType());
            }
        }

        private static Statement RemoveMatchingTrailingTransfer(
            Statement statement, System.Type transferType)
        {
            if (statement == null)
            {
                return null;
            }

            if (statement.GetType() == transferType)
            {
                return new BlockStatement();
            }

            if (statement is BlockStatement block &&
                block.Statements.LastOrDefault() is Statement trailing &&
                trailing.GetType() == transferType)
            {
                block.Statements.RemoveAt(block.Statements.Count - 1);
            }

            return statement;
        }

        /// <summary>
        /// 规范化单个条件节点中由共享分支或循环转移形成的嵌套外壳。
        /// </summary>
        public static void NormalizeConditional(IfStatement conditional)
        {
            if (conditional == null)
            {
                return;
            }

            TryMergeNestedThenCondition(conditional);
            TryFlattenSharedElseBranch(conditional);
            TryMergeContinueGuard(conditional);
        }

        /// <summary>
        /// 将只有单个内层条件的正文折叠为短路 AND。两种写法会以相同顺序、
        /// 相同次数求值条件，因此即使条件带副作用也不会改变语义。
        /// </summary>
        private static bool TryMergeNestedThenCondition(IfStatement outer)
        {
            if (outer.Else != null ||
                UnwrapSingleIf(outer.Then) is not { Else: null } inner)
            {
                return false;
            }

            outer.Condition = UnwrapCondition(outer.Condition)
                .And(UnwrapCondition(inner.Condition));
            outer.Then = inner.Then;
            return true;
        }

        private static bool TryMergeGuardSelectorWithFollowingIf(
            IAstNode selectorNode, IAstNode followingNode, out IfStatement merged)
        {
            merged = null;
            var selector = selectorNode switch
            {
                ConditionExpression condition => condition.Condition,
                ExpressionStatement { Expression: ConditionExpression condition } =>
                    condition.Condition,
                _ => null
            };
            if (selector == null ||
                !ExpressionEffectAnalysis.IsProvablySideEffectFree(selector) ||
                followingNode is not IfStatement following || following.Else != null)
            {
                return false;
            }

            var followingCondition = UnwrapCondition(following.Condition);
            if (followingCondition is not BinaryExpression
                { Op: BinaryOp.LogicOr } disjunction)
            {
                return false;
            }

            var leftUsesSelector = ContainsConditionFactor(disjunction.Left, selector);
            var rightUsesSelector = ContainsConditionFactor(disjunction.Right, selector);
            if (leftUsesSelector == rightUsesSelector)
            {
                return false;
            }

            // 前一条件负责选择 OR 的求值臂，但共享正文结构化后可能只留下一个
            // 裸选择器。把另一臂补上反向 guard，并删除已经吸收的选择器语句。
            var guardedUniqueArm = selector.Invert().And(
                leftUsesSelector ? disjunction.Right : disjunction.Left);
            following.Condition = leftUsesSelector
                ? disjunction.Left.Or(guardedUniqueArm)
                : guardedUniqueArm.Or(disjunction.Right);
            merged = following;
            return true;
        }

        private static bool TryMergeAdjacentLoopTransferIf(
            IAstNode firstNode, IAstNode secondNode, out IfStatement merged)
        {
            merged = null;
            if (firstNode is not IfStatement first ||
                secondNode is not IfStatement second ||
                first.Else != null || second.Else != null ||
                !TryGetSingleLoopTransfer(first.Then, out var firstTransfer) ||
                !TryGetSingleLoopTransfer(second.Then, out var secondTransfer) ||
                firstTransfer.GetType() != secondTransfer.GetType())
            {
                return false;
            }

            var firstCondition = UnwrapCondition(first.Condition);
            var secondCondition = UnwrapCondition(second.Condition);
            if (!ExpressionEffectAnalysis.IsProvablySideEffectFree(firstCondition) ||
                !ExpressionEffectAnalysis.IsProvablySideEffectFree(secondCondition))
            {
                return false;
            }

            // 两个纯条件执行同一种 break/continue 时，可按原顺序合并为短路 OR。
            merged = new IfStatement(
                firstCondition.Or(secondCondition), first.Then, null);
            return true;
        }

        private static bool TryGetSingleLoopTransfer(
            Statement statement, out Statement transfer)
        {
            transfer = null;
            if (statement is BreakStatement or ContinueStatement)
            {
                transfer = statement;
                return true;
            }

            if (statement is not BlockStatement block ||
                block.Statements.Count != 1 ||
                block.Statements[0] is not Statement nested ||
                nested is not BreakStatement && nested is not ContinueStatement)
            {
                return false;
            }

            transfer = nested;
            return true;
        }

        private static bool TryFlattenSharedElseBranch(IfStatement outer)
        {
            var inner = UnwrapSingleIf(outer.Then);
            var emptyOuterElse = UnwrapSingleIf(outer.Else);
            var sharedElse = UnwrapSingleIf(inner?.Else);
            if (inner == null || emptyOuterElse == null || sharedElse == null ||
                !IsStructuralNoOpIfStatement(emptyOuterElse) ||
                IsStructuralNoOpIfStatement(sharedElse))
            {
                return false;
            }

            var emptyCondition = UnwrapCondition(emptyOuterElse.Condition);
            var sharedCondition = UnwrapCondition(sharedElse.Condition);
            if (!ContainsConditionFactor(sharedCondition, emptyCondition))
            {
                return false;
            }

            // CFG 共享形态 C ? (N ? A : B) : B 可能先被还原成带空 else 壳的
            // 嵌套条件。恢复成 (C && N) ? A : B，且不复制任一条件表达式。
            outer.Condition = UnwrapCondition(outer.Condition)
                .And(UnwrapCondition(inner.Condition));
            outer.Then = inner.Then;
            outer.Else = inner.Else;
            if (outer.Else is IfStatement elseIf)
            {
                elseIf.IsElseIf = true;
            }

            return true;
        }

        private static bool TryMergeContinueGuard(IfStatement conditional)
        {
            if (!IsEmptyStatement(conditional.Then) ||
                conditional.Else is not IfStatement elseIf ||
                !IsContinueStatement(elseIf.Then) || elseIf.Else == null)
            {
                return false;
            }

            conditional.Condition = new BinaryExpression(
                conditional.Condition, elseIf.Condition, BinaryOp.LogicOr);
            conditional.Then = new ContinueStatement();
            conditional.Else = elseIf.Else;
            return true;
        }

        private static IfStatement UnwrapSingleIf(Statement statement)
        {
            if (statement is IfStatement direct)
            {
                return direct;
            }

            return statement is BlockStatement { Statements.Count: 1 } block &&
                   block.Statements[0] is IfStatement nested
                ? nested
                : null;
        }

        private static Expression UnwrapCondition(Expression expression)
        {
            return expression is ConditionExpression wrapper
                ? wrapper.Condition
                : expression;
        }

        private static bool ContainsConditionFactor(
            Expression expression, Expression factor)
        {
            if (ExpressionStructuralComparer.AreEquivalent(expression, factor))
            {
                return true;
            }

            return expression is BinaryExpression binary &&
                   binary.Op is BinaryOp.LogicAnd or BinaryOp.LogicOr &&
                   (ContainsConditionFactor(binary.Left, factor) ||
                    ContainsConditionFactor(binary.Right, factor));
        }

        private static bool IsStructuralNoOpIfStatement(IfStatement conditional)
        {
            return conditional != null &&
                   IsNoSideEffectBlock(conditional.Then) &&
                   (conditional.Else == null ||
                    IsNoSideEffectBlock(conditional.Else));
        }

        private static bool IsNoSideEffectBlock(Statement statement)
        {
            if (statement is ContinueStatement or BreakStatement)
            {
                return false;
            }

            if (statement is IfStatement nestedIf)
            {
                return IsStructuralNoOpIfStatement(nestedIf);
            }

            if (statement == null)
            {
                return true;
            }

            if (statement is not BlockStatement block)
            {
                return false;
            }

            foreach (var node in block.Statements)
            {
                if (node is GotoExpression ||
                    node is ConditionExpression rawCondition &&
                    ExpressionEffectAnalysis.IsProvablySideEffectFree(rawCondition))
                {
                    continue;
                }

                if (node is IfStatement childIf)
                {
                    if (!IsStructuralNoOpIfStatement(childIf))
                    {
                        return false;
                    }

                    continue;
                }

                if (node is not ExpressionStatement expressionStatement ||
                    expressionStatement.Expression == null ||
                    ExpressionEffectAnalysis.HasObservableEffect(
                        expressionStatement.Expression))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsEmptyStatement(Statement statement)
        {
            return statement switch
            {
                null => true,
                BlockStatement block =>
                    block.Statements == null || block.Statements.Count == 0,
                _ => false
            };
        }

        private static bool IsContinueStatement(Statement statement)
        {
            return statement switch
            {
                ContinueStatement => true,
                BlockStatement block when block.Statements?.Count == 1 &&
                                          block.Statements[0] is ContinueStatement => true,
                _ => false
            };
        }
    }
}
