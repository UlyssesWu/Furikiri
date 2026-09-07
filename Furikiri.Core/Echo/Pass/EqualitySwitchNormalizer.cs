using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// equality 分派恢复结果。未命中区域有时是 switch 后续语句而非 default，
    /// 因此不能强迫二者共同塞进一个 SwitchStatement。
    /// </summary>
    internal sealed class EqualitySwitchNormalization
    {
        public SwitchStatement Switch { get; set; }
        public Statement Continuation { get; set; }
    }

    /// <summary>
    /// 将结构化 AST 中同一选择值的长相等分派恢复为一等 switch 节点。
    /// </summary>
    /// <remarks>
    /// CFG 阶段已经证明各臂互斥并生成完整 if/else 链。本阶段还要求所有比较
    /// 共享同一条 VM 定义指令产生的选择快照；仅凭标签数量无法区分 switch 与
    /// 连续早退 if。循环体末尾各臂的 continue 等价于 case break 后自然进入
    /// 循环闩锁，因此可恢复为更接近源码的 break。
    /// </remarks>
    internal static class EqualitySwitchNormalizer
    {
        public static bool TryNormalize(
            IfStatement root, bool isLoopTail, out SwitchStatement result)
        {
            result = null;
            if (!TryNormalizeWithContinuation(
                    root, isLoopTail, out var normalization) ||
                normalization.Continuation != null)
            {
                return false;
            }

            result = normalization.Switch;
            return true;
        }

        internal static bool TryNormalizeWithContinuation(
            IfStatement root, bool isLoopTail,
            out EqualitySwitchNormalization result)
        {
            result = null;
            if (root == null || !root.IsEqualityDispatch)
            {
                return false;
            }

            var cases = new List<(
                List<Expression> Labels,
                BlockStatement Body,
                bool FallsThroughToDefault)>();
            Expression selector = null;
            Statement cursor = root;
            Statement defaultBody = null;
            var labelCount = 0;
            while (cursor is IfStatement { IsEqualityDispatch: true } conditional)
            {
                if (!TryReadLabels(
                        conditional.Condition, ref selector, out var labels) ||
                    !TryAsBlock(conditional.Then, out var body) ||
                    ContainsBreak(body))
                {
                    return false;
                }

                labelCount += labels.Count;
                cases.Add((
                    labels,
                    body,
                    conditional.FallsThroughToEqualityDispatchDefault));
                cursor = conditional.Else;
            }

            defaultBody = cursor;
            var sequentialContinuation = default(Statement);
            // 所有 case 都终止时，最后的 else 在语义上既可写成 default，也可
            // 写在 switch 之后。来源指令已证明存在 switch、却没有显式 default
            // 收尾时，应把该区域作为后续语句单独返回，保留声明作用域和源码顺序。
            if (defaultBody != null &&
                cases.All(@case => CaseBodyTerminates(@case.Body)) &&
                root.HasCompilerSwitchEvidence &&
                !root.HasCompilerDefaultEvidence &&
                !root.HasEqualityDispatchDefaultFallthrough)
            {
                sequentialContinuation = defaultBody;
                defaultBody = null;
            }

            // switch 总会先建立只求值一次的选择快照。即使有三个以上标签，
            // 没有该定义身份的链也可能只是连续早退 if，不能靠标签数量猜测。
            if (labelCount < 1 || selector == null ||
                !IsCachedSelector(selector) ||
                defaultBody != null &&
                 (!TryAsBlock(defaultBody, out _) ||
                 ContainsBreak(defaultBody) ||
                 cases.All(@case => CaseBodyTerminates(@case.Body)) &&
                 ContainsDeclaration(defaultBody) &&
                 !root.HasEqualityDispatchDefaultFallthrough &&
                 !root.HasCompilerDefaultEvidence))
            {
                return false;
            }

            var endsAtLoopLatch = isLoopTail ||
                                  root.EqualityDispatchEndsAtLoopLatch;
            // 转换会把 continue 改成 break，并可能重排条件贯穿 case；必须先
            // 验证全部 case 都可恢复，避免后面的某项失败时留下半修改 AST。
            if (defaultBody != null &&
                cases.Any(@case => @case.FallsThroughToDefault &&
                                   !CanRestoreDefaultFallthrough(
                                       @case.Body, endsAtLoopLatch)))
            {
                return false;
            }

            var switchStatement = new SwitchStatement { Expression = selector };
            foreach (var (@caseLabels, body, fallsThroughToDefault) in cases)
            {
                // 没有 default 正文时，未命中目标只是 switch 后的公共继续块。
                // CFG 中各 case 的 break 也会到达该块，不能据此把它们误判为
                // 向 default 贯穿；否则嵌套 switch 的 case 会丢失 break。
                if (fallsThroughToDefault && defaultBody != null)
                {
                    if (!RestoreDefaultFallthrough(body, endsAtLoopLatch))
                    {
                        return false;
                    }
                }
                else if (endsAtLoopLatch)
                {
                    ReplaceTerminalContinue(body);
                }
                else
                {
                    AppendBreakWhenNeeded(body);
                }

                var clause = new SwitchCaseClause { Body = body };
                clause.Labels.AddRange(@caseLabels);
                switchStatement.Cases.Add(clause);
            }

            if (defaultBody != null)
            {
                TryAsBlock(defaultBody, out var defaultBlock);
                if (endsAtLoopLatch)
                {
                    ReplaceTerminalContinue(defaultBlock);
                }
                else
                {
                    AppendBreakWhenNeeded(defaultBlock);
                }

                switchStatement.Default = defaultBlock;
            }

            result = new EqualitySwitchNormalization
            {
                Switch = switchStatement,
                Continuation = sequentialContinuation
            };
            return true;
        }

        private static bool TryReadLabels(
            Expression expression, ref Expression selector,
            out List<Expression> labels)
        {
            labels = new List<Expression>();
            foreach (var item in FlattenOr(expression))
            {
                if (item is not BinaryExpression
                    {
                        Op: BinaryOp.Equal
                    } comparison ||
                    !TrySplitComparison(comparison, out var candidate, out var label))
                {
                    return false;
                }

                if (selector == null)
                {
                    selector = candidate;
                }
                else if (!AreSameSelectorEvaluation(selector, candidate))
                {
                    return false;
                }

                labels.Add(label);
            }

            return labels.Count > 0;
        }

        private static IEnumerable<Expression> FlattenOr(Expression expression)
        {
            if (expression is BinaryExpression { Op: BinaryOp.LogicOr } binary)
            {
                foreach (var item in FlattenOr(binary.Left))
                {
                    yield return item;
                }
                foreach (var item in FlattenOr(binary.Right))
                {
                    yield return item;
                }
                yield break;
            }

            yield return expression;
        }

        private static bool TrySplitComparison(
            BinaryExpression comparison,
            out Expression selector, out Expression label)
        {
            selector = null;
            label = null;
            if (comparison.Right is ConstantExpression)
            {
                selector = comparison.Left;
                label = comparison.Right;
                return IsStableSelector(selector);
            }

            if (comparison.Left is ConstantExpression)
            {
                selector = comparison.Right;
                label = comparison.Left;
                return IsStableSelector(selector);
            }

            // TJS2 的 case 标签允许任意表达式，并会按源码顺序逐个求值。
            // equality 分派计划已证明每个比较的左侧来自同一个选择值，因此
            // 非常量右侧可以原样作为 case 标签，不能把合法的成员/计算式标签
            // 限制成普通 if/else 链。
            if (IsCachedSelector(comparison.Left) && comparison.Right != null)
            {
                selector = comparison.Left;
                label = comparison.Right;
                return true;
            }

            return false;
        }

        private static bool IsStableSelector(Expression expression)
        {
            // switch 只求值一次。仅接受寄存器/标识符/属性读取以及已经由
            // 表达式传播明确标记为缓存结果的表达式，拒绝裸调用等动态求值。
            return expression is LocalExpression or IdentifierExpression or
                   PropertyAccessExpression ||
                   expression?.CachedTemporarySlot.HasValue == true;
        }

        private static bool IsCachedSelector(Expression expression)
        {
            return expression?.CachedTemporarySlot.HasValue == true &&
                   expression.CachedEvaluationId.HasValue;
        }

        private static bool AreSameSelectorEvaluation(
            Expression left, Expression right)
        {
            return EqualityDispatchAnalyzer.AreSameSelectorEvaluation(left, right);
        }

        private static bool TryAsBlock(
            Statement statement, out BlockStatement block)
        {
            if (statement is BlockStatement existing)
            {
                block = existing;
                return true;
            }

            if (statement == null)
            {
                block = new BlockStatement();
                return true;
            }

            block = new BlockStatement();
            block.Statements.Add(statement);
            return true;
        }

        private static bool ContainsBreak(Statement statement)
        {
            return statement switch
            {
                null => false,
                BreakStatement => true,
                BlockStatement block => block.Statements.Any(ContainsBreakNode),
                IfStatement conditional =>
                    ContainsBreak(conditional.Then) || ContainsBreak(conditional.Else),
                WhileStatement or DoWhileStatement or ForStatement => false,
                TryStatement tryStatement =>
                    ContainsBreak(tryStatement.Try) ||
                    ContainsBreak(tryStatement.Finally),
                SwitchStatement => false,
                _ => false
            };
        }

        private static bool ContainsBreakNode(IAstNode node)
        {
            return node is Statement statement && ContainsBreak(statement);
        }

        /// <summary>
        /// 所有 case 都提前终止时，比较链的未命中分支既可能是源码 default，
        /// 也可能是 switch 后的顺序继续代码。若其中建立了新局部，把它压进
        /// default 会改变顶层/外层声明作用域，因此保留原顺序结构更安全。
        /// 非终止 case 能到达公共汇合点时不存在这种歧义，可正常恢复 switch。
        /// </summary>
        private static bool ContainsDeclaration(IAstNode node)
        {
            if (node == null)
            {
                return false;
            }

            if (node is BinaryExpression
                {
                    Op: BinaryOp.Assign,
                    IsDeclaration: true,
                    Left: LocalExpression
                })
            {
                return true;
            }

            if (node is ExpressionStatement expressionStatement)
            {
                return ContainsDeclaration(expressionStatement.Expression);
            }

            return node.Children?.Any(ContainsDeclaration) == true;
        }

        private static void ReplaceTerminalContinue(BlockStatement block)
        {
            if (block?.Statements?.LastOrDefault() is ContinueStatement)
            {
                block.Statements[^1] = new BreakStatement();
                return;
            }

            AppendBreakWhenNeeded(block);
        }

        private static void AppendBreakWhenNeeded(BlockStatement block)
        {
            if (block == null ||
                CaseBodyTerminates(block))
            {
                return;
            }

            block.Statements.Add(new BreakStatement());
        }

        /// <summary>
        /// 循环尾 switch 的 case break 会先被 CFG 恢复成 continue。若该 case
        /// 另有一条路径落入 default，常见 AST 形态是
        /// <c>if (success) body; continue;</c>。将失败路径改为 switch break，
        /// 再把成功正文移到守卫之后，即可恢复源码中的条件 break 与自然贯穿。
        /// </summary>
        private static bool RestoreDefaultFallthrough(
            BlockStatement body, bool isLoopTail)
        {
            if (body?.Statements?.LastOrDefault() is not ContinueStatement)
            {
                // 非循环 case 未生成 continue 外壳时，正文末尾自然进入 default。
                return true;
            }

            if (!isLoopTail || body.Statements.Count < 2 ||
                body.Statements[^2] is not IfStatement
                {
                    Else: null,
                    Then: BlockStatement successBody
                } guard)
            {
                return false;
            }

            body.Statements.RemoveRange(body.Statements.Count - 2, 2);
            var breakBody = new BlockStatement();
            breakBody.Statements.Add(new BreakStatement());
            body.Statements.Add(new IfStatement(
                guard.Condition.Invert(), breakBody, null));
            body.Statements.AddRange(successBody.Statements);
            return true;
        }

        private static bool CanRestoreDefaultFallthrough(
            BlockStatement body, bool isLoopTail)
        {
            return body?.Statements?.LastOrDefault() is not ContinueStatement ||
                   isLoopTail && body.Statements.Count >= 2 &&
                   body.Statements[^2] is IfStatement
                   {
                       Else: null,
                       Then: BlockStatement
                   };
        }

        private static bool CaseBodyTerminates(IAstNode node)
        {
            return node switch
            {
                BreakStatement or ContinueStatement => true,
                BlockStatement { Statements.Count: > 0 } block =>
                    CaseBodyTerminates(block.Statements[^1]),
                IfStatement { Else: not null } conditional =>
                    CaseBodyTerminates(conditional.Then) &&
                    CaseBodyTerminates(conditional.Else),
                _ => StructuredControlFlowFacts.AlwaysTerminates(node)
            };
        }
    }
}
