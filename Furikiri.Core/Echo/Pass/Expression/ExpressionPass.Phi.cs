using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private static void TryAnnotateConditionalPhi(Block mergeBlock, List<Block> froms, PhiExpression phi)
        {
            if (phi == null || froms == null || froms.Count != phi.PossibleExpressions.Count)
            {
                return;
            }

            // 普通值的三路 Phi 必须先按完整决策图求值。仅用“某条件能否到达
            // 哪些前驱”来划分内外层，在短路路径重新汇合时会把中间守卫漏掉。
            // FlagReg 仍优先走下面更紧凑的布尔形态恢复，失败后再使用决策图。
            if ((froms.Count > 3 ||
                 froms.Count == 3 &&
                 (phi.Slot != Const.FlagReg ||
                  phi.PossibleExpressions.All(IsInvocationValue))) &&
                TryAnnotateDecisionGraphPhi(mergeBlock, froms, phi))
            {
                return;
            }

            if (froms.Count == 3 && TryAnnotateNestedConditionalPhi(mergeBlock, froms, phi))
            {
                return;
            }

            if (froms.Count == 3 && TryAnnotateDecisionGraphPhi(mergeBlock, froms, phi))
            {
                return;
            }

            static bool IsInvocationValue(Expression expression)
            {
                return expression is InvokeExpression ||
                       expression is PhiExpression nested &&
                       nested.PossibleExpressions.All(IsInvocationValue);
            }

            if (froms.Count != 2)
            {
                return;
            }

            // A flag-producing predecessor can also branch onward to the other
            // predecessor. Its value applies only to its edge into the merge,
            // not to every path entering it: (A || B) && C needs C after B=true.
            if (phi.Slot == Const.FlagReg &&
                TryAnnotateDecisionGraphPhi(mergeBlock, froms, phi))
            {
                return;
            }

            if (TryAnnotateDecisionChainPhi(mergeBlock, froms, phi))
            {
                return;
            }

            static bool TryAssignFromCondition(ConditionExpression cond, List<Block> preds, PhiExpression targetPhi, Block targetMerge)
            {
                if (cond?.Condition == null)
                {
                    return false;
                }

                // 直接跳到 merge：沿用原有逻辑
                if (cond.TrueBranch == targetMerge.Start || cond.FalseBranch == targetMerge.Start)
                {
                    return false;
                }

                var trueIdx = preds.FindIndex(b => b.Start == cond.TrueBranch);
                var falseIdx = preds.FindIndex(b => b.Start == cond.FalseBranch);
                if (trueIdx < 0 || falseIdx < 0 || trueIdx == falseIdx)
                {
                    return false;
                }

                targetPhi.Condition = cond;
                targetPhi.ThenBranch = targetPhi.PossibleExpressions[trueIdx];
                targetPhi.ElseBranch = targetPhi.PossibleExpressions[falseIdx];
                return true;
            }

            static bool TryAssignFromReachability(
                Block conditionBlock, List<Block> preds,
                PhiExpression targetPhi, Block targetMerge)
            {
                var condition = conditionBlock?.Statements?.GetCondition();
                if (condition == null || conditionBlock.To.Count != 2)
                {
                    return false;
                }

                bool CanReach(Block start, Block target)
                {
                    var pending = new Queue<Block>();
                    var visited = new HashSet<Block>();
                    pending.Enqueue(start);
                    while (pending.Count > 0)
                    {
                        var current = pending.Dequeue();
                        if (!visited.Add(current) || current == targetMerge)
                        {
                            continue;
                        }

                        if (current == target)
                        {
                            return true;
                        }

                        foreach (var next in current.To)
                        {
                            pending.Enqueue(next);
                        }
                    }

                    return false;
                }

                var trueHead = conditionBlock.To.FirstOrDefault(block => block.Start == condition.TrueBranch);
                var falseHead = conditionBlock.To.FirstOrDefault(block => block.Start == condition.FalseBranch);
                if (trueHead == null || falseHead == null)
                {
                    return false;
                }

                var trueIndices = preds
                    .Select((block, index) => (block, index))
                    .Where(item => CanReach(trueHead, item.block))
                    .Select(item => item.index)
                    .ToList();
                var falseIndices = preds
                    .Select((block, index) => (block, index))
                    .Where(item => CanReach(falseHead, item.block))
                    .Select(item => item.index)
                    .ToList();
                if (trueIndices.Count != 1 || falseIndices.Count != 1 ||
                    trueIndices[0] == falseIndices[0])
                {
                    return false;
                }

                targetPhi.Condition = new ConditionExpression(condition.Condition, condition.JumpIf);
                targetPhi.ThenBranch = targetPhi.PossibleExpressions[trueIndices[0]];
                targetPhi.ElseBranch = targetPhi.PossibleExpressions[falseIndices[0]];
                return true;
            }
            
            static bool TryBuildShortCircuitAndCondition(Block condBlock, Block elsePred, Expression innerCond, out Expression combined)
            {
                static bool CanReach(
                    Block current, Block target, Block blocked,
                    int depth, HashSet<Block> visited)
                {
                    if (current == null || target == null || depth < 0 || !visited.Add(current))
                    {
                        return false;
                    }

                    if (current.Start == target.Start)
                    {
                        return true;
                    }

                    if (current == blocked)
                    {
                        return false;
                    }

                    if (depth == 0)
                    {
                        return false;
                    }

                    foreach (var next in current.To)
                    {
                        if (CanReach(next, target, blocked, depth - 1, visited))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                static bool BranchFlowsTo(
                    Block conditionBlock, int branchStart, Block target,
                    Block blocked = null)
                {
                    if (conditionBlock == null || target == null)
                    {
                        return false;
                    }

                    if (branchStart == target.Start)
                    {
                        return true;
                    }

                    var branchHead = conditionBlock.To.FirstOrDefault(b => b.Start == branchStart);
                    if (branchHead == null)
                    {
                        return false;
                    }

                    return CanReach(branchHead, target, blocked, 6, new HashSet<Block>());
                }

                combined = null;
                if (condBlock == null || elsePred == null || innerCond == null)
                {
                    return false;
                }

                var candidates = elsePred.From
                    .Concat(elsePred.From.SelectMany(b => b.From))
                    .Distinct();
                foreach (var parent in candidates)
                {
                    if (parent == null || ReferenceEquals(parent, condBlock) ||
                        parent.Statements?.Any(s => s is ConditionExpression) != true)
                    {
                        continue;
                    }

                    var parentCond = (ConditionExpression)parent.Statements.Last(s => s is ConditionExpression);
                    var trueToCond = BranchFlowsTo(parent, parentCond.TrueBranch, condBlock);
                    var falseToCond = BranchFlowsTo(parent, parentCond.FalseBranch, condBlock);
                    // 旁路必须在不经过内层条件块的情况下到达 else 前驱；若两条
                    // 路径先汇合后才执行内层条件，它们只是顺序 if，不能合成 &&。
                    var trueToElse = BranchFlowsTo(
                        parent, parentCond.TrueBranch, elsePred, condBlock);
                    var falseToElse = BranchFlowsTo(
                        parent, parentCond.FalseBranch, elsePred, condBlock);
                    if (!(trueToCond && falseToElse) && !(falseToCond && trueToElse))
                    {
                        continue;
                    }

                    var outerCond = parentCond.Condition;
                    if (falseToCond)
                    {
                        outerCond = new UnaryExpression(outerCond, UnaryOp.Not);
                    }

                    combined = new BinaryExpression(outerCond, innerCond, BinaryOp.LogicAnd);
                    return true;
                }

                return false;
            }

            var condPred = froms.FirstOrDefault(b => b.Statements?.Any(s => s is ConditionExpression) == true);
            if (condPred != null)
            {
                var cond = (ConditionExpression)condPred.Statements.Last(s => s is ConditionExpression);
                if (TryAssignFromCondition(cond, froms, phi, mergeBlock))
                {
                    var falsePred = froms.FirstOrDefault(b => b.Start == cond.FalseBranch);
                    // 对 FlagReg 和非 FlagReg（三元结果寄存器）均应用短路与条件合并，
                    // 以确保形如 (outerCond && innerCond) ? A : B 的三元条件被完整重建
                    if (TryBuildShortCircuitAndCondition(condPred, falsePred, cond.Condition, out var combinedCond))
                    {
                        phi.Condition = new ConditionExpression(combinedCond, cond.JumpIf)
                        {
                            JumpTo = cond.JumpTo,
                            ElseTo = cond.ElseTo
                        };
                    }
                    return;
                }

                // 兼容旧路径：条件块直接跳到 merge
                if (cond.TrueBranch != mergeBlock.Start && cond.FalseBranch != mergeBlock.Start)
                {
                    return;
                }

                var condPredIdx = froms.IndexOf(condPred);
                var otherIdx = condPredIdx == 0 ? 1 : 0;
                var condPredValue = phi.PossibleExpressions[condPredIdx];
                var otherValue = phi.PossibleExpressions[otherIdx];
                var trueToMerge = cond.TrueBranch == mergeBlock.Start;

                // 使用 cond 的浅拷贝，避免 ControlFlowPass 的 MergeIfCondition 修改原始
                // ConditionExpression.Condition 时影响 phi 的短路语义判断（ThenBranch/cond 引用关系）
                phi.Condition = new ConditionExpression(cond.Condition, cond.JumpIf)
                {
                    JumpTo = cond.JumpTo,
                    ElseTo = cond.ElseTo
                };
                phi.ThenBranch = trueToMerge ? condPredValue : otherValue;
                phi.ElseBranch = trueToMerge ? otherValue : condPredValue;
                return;
            }

            // 菱形分支：merge 的前驱不带条件，条件位于共同上游块
            var parentCond = froms
                .SelectMany(b => b.From)
                .Distinct()
                .FirstOrDefault(b => b.Statements?.Any(s => s is ConditionExpression) == true);
            if (parentCond == null)
            {
                return;
            }

            var parentCondition = (ConditionExpression)parentCond.Statements.Last(s => s is ConditionExpression);
            if (!TryAssignFromCondition(parentCondition, froms, phi, mergeBlock))
            {
                if (!TryAssignFromReachability(parentCond, froms, phi, mergeBlock))
                {
                    return;
                }
            }

            var parentFalsePred = froms.FirstOrDefault(b => b.Start == parentCondition.FalseBranch);
            // 对所有寄存器均应用短路与条件合并，以完整重建菱形分支模式下的短路三元条件
            if (TryBuildShortCircuitAndCondition(parentCond, parentFalsePred, parentCondition.Condition, out var parentCombinedCond))
            {
                phi.Condition = new ConditionExpression(parentCombinedCond, parentCondition.JumpIf)
                {
                    JumpTo = parentCondition.JumpTo,
                    ElseTo = parentCondition.ElseTo
                };
            }
        }
    }
}
