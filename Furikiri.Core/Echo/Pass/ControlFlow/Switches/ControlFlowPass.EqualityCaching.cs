using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        /// <summary>
        /// switch 的控制表达式在 TJS2 中只求值一次。字节码会把调用结果保存到
        /// 临时寄存器再连续比较，但表达式传播可能把同一个调用展开到每个
        /// else-if。重新引入局部变量，避免 getter/函数被重复执行。
        /// </summary>
        private void HoistRepeatedInvocationComparisonTarget(
            Block root, IEnumerable<Block> conditionBlocks)
        {
            if (root == null || conditionBlocks == null)
            {
                return;
            }

            var groups = conditionBlocks
                .Select(block => block?.Statements.GetCondition()?.Condition)
                .OfType<BinaryExpression>()
                .Where(comparison =>
                    comparison.Op is BinaryOp.Equal or BinaryOp.Congruent &&
                    comparison.Left is InvokeExpression &&
                    comparison.Left.CachedEvaluationId.HasValue)
                // 临时槽会被不同调用复用，必须按定义指令分组；源码中两次独立
                // 写出的同名调用即使落到同一槽，也不能合并求值。
                .GroupBy(comparison => comparison.Left.CachedEvaluationId.Value)
                .Where(group => group.Count() > 1)
                .ToList();
            foreach (var group in groups)
            {
                var invocation = group.First().Left;
                var local = CreateSyntheticLocal();
                local.CachedTemporarySlot = invocation.CachedTemporarySlot;
                local.CachedEvaluationId = invocation.CachedEvaluationId;
                var rootCondition = root.Statements.GetCondition();
                var rootConditionIndex = root.Statements.IndexOf(rootCondition);
                root.Statements.Insert(rootConditionIndex < 0
                        ? root.Statements.Count
                        : rootConditionIndex,
                    new BinaryExpression(local, invocation, BinaryOp.Assign)
                    {
                        IsDeclaration = true
                    });
                foreach (var comparison in group)
                {
                    comparison.Left = local;
                }
            }
        }

        /// <summary>
        /// 在结构化开始前查找“同一缓存调用结果连续与多个常量比较”的线性链。
        /// 这正是 switch 控制表达式的常见字节码形态；提前缓存可覆盖循环内因
        /// break/continue 不能走通用 equality 恢复的分派。
        /// </summary>
        private void HoistLinearCachedInvocationEqualityChains()
        {
            if (_context?.BlockTable == null)
            {
                return;
            }

            InvokeExpression ComparedInvocation(Block block)
            {
                return block?.Statements.GetCondition()?.Condition is BinaryExpression
                {
                    Op: BinaryOp.Equal or BinaryOp.Congruent,
                    Left: InvokeExpression invocation
                }
                    ? invocation
                    : null;
            }

            bool IsSameEvaluation(InvokeExpression left, InvokeExpression right)
            {
                if (left == null || right == null)
                {
                    return false;
                }

                if (left.CachedEvaluationId.HasValue || right.CachedEvaluationId.HasValue)
                {
                    return left.CachedEvaluationId.HasValue &&
                           left.CachedEvaluationId == right.CachedEvaluationId;
                }

                // 某些 switch 控制调用不会先写入临时槽，而是为每个 case 重复
                // 发出 CALLD。调用本身及接收者、实参均相同时，后续再结合 CFG
                // 的“命中后不回到比较链”约束判断它是否确属一次 switch 求值。
                return left.InvokeType == right.InvokeType &&
                       string.Equals(left.MethodName, right.MethodName,
                           StringComparison.Ordinal) &&
                       AreTextuallyEquivalent(left.MethodExpression, right.MethodExpression) &&
                       AreTextuallyEquivalent(left.Instance, right.Instance) &&
                       left.HasOmittedArguments == right.HasOmittedArguments &&
                       left.Parameters.Count == right.Parameters.Count &&
                       left.Parameters.Zip(right.Parameters,
                               (l, r) => AreTextuallyEquivalent(l, r))
                           .All(equal => equal);
            }

            var processed = new HashSet<Block>();
            foreach (var root in _context.Blocks.OrderBy(block => block.Start).ToList())
            {
                var invocation = ComparedInvocation(root);
                if (invocation == null || processed.Contains(root))
                {
                    continue;
                }

                // 若某个同一次求值的 false 边直达当前块，它不是链首。
                var hasPrevious = root.From.Any(predecessor =>
                {
                    if (!IsSameEvaluation(ComparedInvocation(predecessor), invocation))
                    {
                        return false;
                    }

                    var condition = predecessor.Statements.GetCondition();
                    return _context.BlockTable.TryGetValue(
                               condition.FalseBranch, out var falseTarget) &&
                           NormalizeDecisionTarget(falseTarget, null) == root;
                });
                if (hasPrevious)
                {
                    continue;
                }

                var chain = new List<Block>();
                var current = root;
                while (current != null &&
                       IsSameEvaluation(ComparedInvocation(current), invocation) &&
                       !chain.Contains(current))
                {
                    chain.Add(current);
                    var condition = current.Statements.GetCondition();
                    if (!_context.BlockTable.TryGetValue(
                            condition.FalseBranch, out var falseTarget))
                    {
                        break;
                    }

                    current = NormalizeDecisionTarget(falseTarget, null);
                }

                if (chain.Count < 2)
                {
                    continue;
                }

                if (!invocation.CachedTemporarySlot.HasValue)
                {
                    var chainSet = chain.ToHashSet();
                    var hitTargets = new List<Block>();
                    var isSelectionChain = true;
                    foreach (var caseBlock in chain)
                    {
                        var condition = caseBlock.Statements.GetCondition();
                        var hitTarget = NormalizeDecisionTarget(
                            _context.BlockTable[condition.TrueBranch], null);
                        hitTargets.Add(hitTarget);

                        // 普通短路式 `(call()==A && X) || call()==B` 在第一次命中
                        // 后仍会回到后续同调用比较；switch 的任一 case 命中后只会
                        // 进入该 case 正文或公共出口。用此差异避免错误合并独立调用。
                        if (chainSet.Any(next => next != caseBlock &&
                                                CanReach(hitTarget, next)))
                        {
                            isSelectionChain = false;
                            break;
                        }
                    }

                    if (!isSelectionChain || hitTargets.Distinct().Count() < 2)
                    {
                        continue;
                    }
                }

                var local = CreateSyntheticLocal();
                local.CachedTemporarySlot = invocation.CachedTemporarySlot;
                local.CachedEvaluationId = invocation.CachedEvaluationId;
                var rootCondition = root.Statements.GetCondition();
                var rootConditionIndex = root.Statements.IndexOf(rootCondition);
                root.Statements.Insert(rootConditionIndex < 0
                        ? root.Statements.Count
                        : rootConditionIndex,
                    new BinaryExpression(local, invocation, BinaryOp.Assign)
                    {
                        IsDeclaration = true
                    });
                foreach (var caseBlock in chain)
                {
                    if (caseBlock.Statements.GetCondition()?.Condition is
                        BinaryExpression comparison)
                    {
                        comparison.Left = local;
                    }
                }

                foreach (var block in chain)
                {
                    processed.Add(block);
                }
            }
        }

        private static bool AreTextuallyEquivalent(Expression left, Expression right)
        {
            return ReferenceEquals(left, right) ||
                   left != null && right != null && left.GetType() == right.GetType() &&
                   string.Equals(left.ToString(), right.ToString(),
                       StringComparison.Ordinal);
        }

        /// <summary>
        /// 为控制流恢复阶段重新引入的求值缓存分配一个不会与原 VM 局部冲突的
        /// 变量。新命名模式使用下一个连续 vN，旧模式仍按绝对槽位输出。
        /// </summary>
        private LocalExpression CreateSyntheticLocal()
        {
            var lowestSlot = _context.Vars.Keys
                .Where(slot => slot <= Const.ArgBase)
                .DefaultIfEmpty((short)Const.ArgBase)
                .Min();
            var slot = checked((short)(lowestSlot - 1));
            while (_context.Vars.ContainsKey(slot))
            {
                slot = checked((short)(slot - 1));
            }

            var nextIndex = _context.Vars.Values
                .Where(variable => !variable.IsParameter && variable.GeneratedIndex.HasValue)
                .Select(variable => variable.GeneratedIndex.Value)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            var variable = new Variable(slot, _context.Object)
            {
                GeneratedIndex = nextIndex
            };
            _context.Vars[slot] = variable;
            return new LocalExpression(variable);
        }
    }
}
