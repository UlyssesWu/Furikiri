using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        /// <summary>
        /// 三元返回的条件可能仍引用一个在前驱块中赋值的临时值，例如
        /// `name != "" &amp;&amp; (index = text.indexOf(name)) &gt; 0`。Phi 已恢复出
        /// 三元式后，把赋值按右侧依赖内联回对应条件，随后原条件 CFG 才能安全隐藏。
        /// </summary>
        private void InlineAssignmentsIntoConditionalPhiGuards()
        {
            var phis = new List<(PhiExpression Phi, Block ReturnBlock)>();
            void CollectPhis(Expression expression, Block returnBlock)
            {
                if (expression == null)
                {
                    return;
                }

                if (expression is PhiExpression phi && phi.IsConditional)
                {
                    phis.Add((phi, returnBlock));
                    CollectPhis(phi.ThenBranch, returnBlock);
                    CollectPhis(phi.ElseBranch, returnBlock);
                    return;
                }

                foreach (var child in expression.Children?.OfType<Expression>() ??
                                      Enumerable.Empty<Expression>())
                {
                    CollectPhis(child, returnBlock);
                }
            }

            foreach (var returnBlock in _context.Blocks)
            {
                foreach (var ret in (returnBlock.Statements ?? new List<IAstNode>())
                             .OfType<ReturnExpression>())
                {
                    CollectPhis(ret.Return, returnBlock);
                }
            }
            if (phis.Count == 0)
            {
                return;
            }

            static HashSet<int> LocalSlots(Expression expression)
            {
                var result = new HashSet<int>();
                void Visit(Expression current)
                {
                    if (current == null)
                    {
                        return;
                    }
                    if (current is LocalExpression local)
                    {
                        result.Add(local.Slot);
                    }

                    IEnumerable<Expression> Children(Expression value) => value switch
                    {
                        InvokeExpression invoke => new[]
                            {
                                invoke.Instance, invoke.MethodExpression
                            }.Where(child => child != null).Concat(invoke.Parameters),
                        BinaryExpression binary => new[] { binary.Left, binary.Right },
                        UnaryExpression unary => new[] { unary.Target },
                        ConditionExpression conditional => new[] { conditional.Condition },
                        PropertyAccessExpression property => new[]
                            { property.Instance, property.Property },
                        PhiExpression phi when phi.IsConditional => new[]
                            { phi.Condition?.Condition, phi.ThenBranch, phi.ElseBranch }
                            .Where(child => child != null),
                        _ => value.Children?.OfType<Expression>() ??
                             Enumerable.Empty<Expression>()
                    };
                    foreach (var child in Children(current))
                    {
                        Visit(child);
                    }
                }
                Visit(expression);
                return result;
            }

            var affectedReturns = new HashSet<Block>();
            foreach (var block in _context.Blocks.OrderBy(candidate => candidate.Start))
            {
                if (block.Statements.Count != 2 ||
                    block.Statements[0] is not BinaryExpression assignment ||
                    assignment.Op != BinaryOp.Assign ||
                    assignment.Left is not LocalExpression target ||
                    block.Statements[1] is not ConditionExpression condition ||
                    !ReferencesAssignmentTarget(condition, assignment.Left))
                {
                    continue;
                }

                var rightSlots = LocalSlots(assignment.Right);
                var matching = phis.FirstOrDefault(entry =>
                {
                    var conditionSlots = LocalSlots(entry.Phi.Condition.Condition);
                    return conditionSlots.Contains(target.Slot) &&
                           rightSlots.Any(conditionSlots.Contains);
                });
                if (matching.Phi == null)
                {
                    continue;
                }

                var replaced = false;
                matching.Phi.Condition.Condition = ReplaceConditionTarget(
                    matching.Phi.Condition.Condition, assignment.Left, assignment, ref replaced);
                if (!replaced)
                {
                    continue;
                }

                assignment.IsDeclaration = false;
                block.Statements.RemoveAt(0);
                affectedReturns.Add(matching.ReturnBlock);
                phis.Remove(matching);
            }

            // 赋值已进入三元条件，沿途的条件/跳转块只剩控制流外壳。保留入口
            // 的变量声明和最终 return，清除中间外壳，防止后续再次恢复成 if。
            foreach (var returnBlock in affectedReturns)
            {
                foreach (var block in _context.Blocks.Where(candidate =>
                             candidate != returnBlock &&
                             candidate.Start < returnBlock.Start &&
                             CanReach(candidate, returnBlock)))
                {
                    block.Statements.RemoveAll(statement =>
                        statement is ConditionExpression or GotoExpression);
                    block.Hidden = block.Statements.Count == 0;
                }
                returnBlock.Hidden = false;
            }
        }

        private void InlineLocalAssignmentsIntoComparisons()
        {
            foreach (var block in _context.Blocks.Where(candidate => !candidate.Hidden))
            {
                if (block.Statements?.Count != 2 ||
                    block.Statements[0] is not BinaryExpression { Op: BinaryOp.Assign, Left: LocalExpression } assignment ||
                    block.Statements[1] is not ConditionExpression condition ||
                    condition.Condition is not BinaryExpression comparison ||
                    !ReferencesAssignmentTarget(comparison.Left, assignment.Left) ||
                    comparison.Left is not LocalExpression ||
                    ReferencesAssignmentTarget(comparison.Right, assignment.Left) ||
                    !block.From.Any(predecessor => predecessor.Statements?.GetCondition() != null &&
                        predecessor.To.Any(target => target != block && block.To.Contains(target))) ||
                    assignment.IsDeclaration && !HasEarlierDominatingDeclaration(block, assignment))
                    continue;

                // Evaluate the assignment exactly once, before the comparison's
                // other operand. This exposes assignment-bearing short circuits
                // without treating the assigned branch as an independent body.
                assignment.IsDeclaration = false;
                comparison.Left = assignment;
                assignment.Parent = comparison;
                block.Statements.RemoveAt(0);
            }
        }

        private void InlineLoopHeaderAssignment(Block conditionBlock, ref Expression condition)
        {
            if (conditionBlock?.Statements == null || condition == null)
            {
                return;
            }

            var currentCondition = condition;
            var conditionIndex = conditionBlock.Statements.FindIndex(node => ReferenceEquals(node, currentCondition));
            if (conditionIndex < 0)
            {
                conditionIndex = conditionBlock.Statements.FindIndex(node => node is ConditionExpression);
            }

            if (conditionIndex <= 0)
            {
                return;
            }

            // `while ((value = next()) !== void)` 会编译成循环头中的赋值语句和
            // 随后的比较。若直接把比较拿去当 while 条件，赋值会落入循环体，首次
            // 检查就读取尚未更新的 value。仅在条件确实读取同一赋值目标时内联。
            for (var i = conditionIndex - 1; i >= 0; i--)
            {
                if (conditionBlock.Statements[i] is UnaryExpression unary &&
                    unary.Op is UnaryOp.Inc or UnaryOp.Dec &&
                    ReferencesAssignmentTarget(condition, unary.Target))
                {
                    // 指令先完成增减、下一条比较再读取新值；内联到条件后必须使用
                    // 前置形式。保留默认后置形式会把 `++i < n` 改成 `i++ < n`。
                    unary.IsPrefix = true;
                    var unaryReplaced = false;
                    condition = ReplaceConditionTarget(
                        condition, unary.Target, unary, ref unaryReplaced);
                    if (unaryReplaced)
                    {
                        conditionBlock.Statements.RemoveAt(i);
                    }
                    return;
                }

                if (conditionBlock.Statements[i] is not BinaryExpression assignment ||
                    assignment.Op != BinaryOp.Assign ||
                    !ReferencesAssignmentTarget(condition, assignment.Left))
                {
                    continue;
                }

                if (assignment.IsDeclaration)
                {
                    var hasEarlierDeclaration =
                        HasEarlierDominatingDeclaration(conditionBlock, assignment);
                    if (!hasEarlierDeclaration)
                    {
                        // 首次声明属于循环体本身，不能生成非法的 `while (var x = ...)`，
                        // 也不能把声明提升出每轮执行的位置。
                        continue;
                    }

                    assignment.IsDeclaration = false;
                }

                var replaced = false;
                condition = ReplaceConditionTarget(condition, assignment.Left, assignment, ref replaced);
                if (replaced)
                {
                    conditionBlock.Statements.RemoveAt(i);
                }
                return;
            }
        }

        private bool HasEarlierDominatingDeclaration(
            Block block, BinaryExpression assignment)
        {
            if (block == null || assignment == null)
            {
                return false;
            }

            return _context.Blocks.Any(candidate =>
                candidate != block && candidate.Start < block.Start &&
                block.Dominator != null && block.Dominator[candidate.Id] &&
                candidate.Statements.OfType<BinaryExpression>().Any(previous =>
                    previous.IsDeclaration && previous.Op == BinaryOp.Assign &&
                    IsSameAssignmentTarget(previous.Left, assignment.Left)));
        }

        /// <summary>
        /// 短路链中允许一种严格的带载荷条件块：一条赋值紧跟一条读取同一目标的
        /// 条件。将纯右值赋值内联后，`ui = source; ui === void` 可恢复为
        /// `(ui = source) === void`，该块也就能继续参与前后的 `||`/`&&` 合并。
        /// 这一等价关系只对 VM 局部槽成立：对象成员的“写入后再读取”可能
        /// 分别触发 setter/getter，不能用赋值表达式的右值代替后一次读取。
        /// 调用、修改型右值等可观察求值不能内联：路径谓词可能在共享决策图中复制该子式，
        /// 使 VM 原本只求值一次的载荷变成多次。此时保留独立赋值，由普通区域
        /// 结构化保存单次求值和后续所有分支。
        /// </summary>
        private void InlineShortCircuitAssignment(Block conditionBlock)
        {
            if (conditionBlock?.Statements?.Count != 2 ||
                conditionBlock.Statements[0] is not BinaryExpression assignment ||
                assignment.Op != BinaryOp.Assign ||
                assignment.Left is not LocalExpression ||
                conditionBlock.Statements[1] is not ConditionExpression condition ||
                ExpressionEffectAnalysis.HasObservableEffect(assignment.Right))
            {
                return;
            }

            if (!ReferencesAssignmentTarget(condition, assignment.Left))
            {
                // Propagation can compare the RHS local directly, leaving the
                // saved handler assignment in front of the short-circuit test.
                // Keep that assignment on the same predicate path.
                if (assignment.Right is not LocalExpression ||
                    !ReferencesAssignmentTarget(condition, assignment.Right) ||
                    assignment.IsDeclaration &&
                    !HasEarlierDominatingDeclaration(conditionBlock, assignment)) return;
                assignment.IsDeclaration = false;
                var replaced = false;
                ReplaceConditionTarget(condition, assignment.Right, assignment, ref replaced);
                if (replaced) conditionBlock.Statements.RemoveAt(0);
                return;
            }

            Expression mergedCondition = condition;
            InlineLoopHeaderAssignment(conditionBlock, ref mergedCondition);
        }

        private static Expression ReplaceConditionTarget(Expression expression, Expression target,
            Expression replacement, ref bool replaced)
        {
            if (expression == null || replaced)
            {
                return expression;
            }

            if (IsSameAssignmentTarget(expression, target))
            {
                replaced = true;
                return replacement;
            }

            switch (expression)
            {
                case ConditionExpression conditional:
                    conditional.Condition = ReplaceConditionTarget(conditional.Condition, target, replacement, ref replaced);
                    if (conditional.Condition != null)
                    {
                        conditional.Condition.Parent = conditional;
                    }
                    break;
                case BinaryExpression binary:
                    binary.Left = ReplaceConditionTarget(binary.Left, target, replacement, ref replaced);
                    if (binary.Left != null)
                    {
                        binary.Left.Parent = binary;
                    }
                    if (!replaced)
                    {
                        binary.Right = ReplaceConditionTarget(binary.Right, target, replacement, ref replaced);
                        if (binary.Right != null)
                        {
                            binary.Right.Parent = binary;
                        }
                    }
                    break;
                case UnaryExpression unary:
                    unary.Target = ReplaceConditionTarget(unary.Target, target, replacement, ref replaced);
                    if (unary.Target != null)
                    {
                        unary.Target.Parent = unary;
                    }
                    break;
            }

            return expression;
        }

        private static bool IsSameAssignmentTarget(Expression left, Expression right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is LocalExpression leftLocal && right is LocalExpression rightLocal)
            {
                return leftLocal.Slot == rightLocal.Slot;
            }

            if (left is IdentifierExpression leftIdentifier && right is IdentifierExpression rightIdentifier)
            {
                return string.Equals(leftIdentifier.FullName, rightIdentifier.FullName, System.StringComparison.Ordinal);
            }

            return false;
        }

        private static bool ReferencesAssignmentTarget(Expression expression, Expression target)
        {
            if (expression == null)
            {
                return false;
            }

            if (IsSameAssignmentTarget(expression, target))
            {
                return true;
            }

            switch (expression)
            {
                case ConditionExpression condition:
                    return ReferencesAssignmentTarget(condition.Condition, target);
                case BinaryExpression binary:
                    return ReferencesAssignmentTarget(binary.Left, target) ||
                           ReferencesAssignmentTarget(binary.Right, target);
                case UnaryExpression unary:
                    return ReferencesAssignmentTarget(unary.Target, target);
                case PropertyAccessExpression property:
                    return ReferencesAssignmentTarget(property.Instance, target) ||
                           ReferencesAssignmentTarget(property.Property, target);
                case IdentifierExpression identifier:
                    return ReferencesAssignmentTarget(identifier.Instance, target);
                case InvokeExpression invoke:
                    return ReferencesAssignmentTarget(invoke.Instance, target) ||
                           ReferencesAssignmentTarget(invoke.MethodExpression, target) ||
                           invoke.Parameters.Any(parameter => ReferencesAssignmentTarget(parameter, target));
                case PhiExpression phi:
                    return phi.PossibleExpressions.Any(possible => ReferencesAssignmentTarget(possible, target));
                default:
                    return false;
            }
        }
    }
}
