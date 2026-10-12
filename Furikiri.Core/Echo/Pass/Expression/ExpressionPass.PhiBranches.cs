using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        /// <summary>
        /// 恢复三路 Phi 的常见嵌套三元形态：外层条件的一侧直接给值，
        /// 另一侧再由内层条件二选一，例如 a ? (b ? x : y) : z。
        /// </summary>
        private static bool TryAnnotateNestedConditionalPhi(
            Block mergeBlock, List<Block> froms, PhiExpression phi)
        {
            bool CanReach(Block start, Block target)
            {
                var pending = new Queue<Block>();
                var visited = new HashSet<Block>();
                pending.Enqueue(start);
                while (pending.Count > 0)
                {
                    var current = pending.Dequeue();
                    if (!visited.Add(current) || current == mergeBlock)
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

            bool BranchReaches(Block owner, Block branchHead, Block predecessor)
            {
                // 短路布尔表达式常由条件块直接跳到 merge；此时该路径的
                // Phi 值保存在条件块自己的最终状态中，而不是独立取值块。
                return branchHead == mergeBlock
                    ? predecessor == owner
                    : CanReach(branchHead, predecessor);
            }

            var values = froms
                .Select((block, index) => (block, value: phi.PossibleExpressions[index]))
                .ToDictionary(item => item.block, item => item.value);
            var conditionCandidates = new HashSet<Block>();
            var backward = new Queue<(Block Block, int Depth)>();
            foreach (var predecessor in froms)
            {
                backward.Enqueue((predecessor, 0));
            }

            while (backward.Count > 0)
            {
                var (current, depth) = backward.Dequeue();
                if (depth > 3)
                {
                    continue;
                }

                if (current.Statements?.GetCondition() != null)
                {
                    conditionCandidates.Add(current);
                }

                foreach (var previous in current.From)
                {
                    backward.Enqueue((previous, depth + 1));
                }
            }

            foreach (var outerBlock in conditionCandidates.OrderBy(block => block.Start))
            {
                var outerCondition = outerBlock.Statements.GetCondition();
                var trueHead = outerBlock.To.FirstOrDefault(block => block.Start == outerCondition.TrueBranch);
                var falseHead = outerBlock.To.FirstOrDefault(block => block.Start == outerCondition.FalseBranch);
                if (trueHead == null || falseHead == null)
                {
                    continue;
                }

                var truePreds = froms
                    .Where(predecessor => BranchReaches(outerBlock, trueHead, predecessor))
                    .ToList();
                var falsePreds = froms
                    .Where(predecessor => BranchReaches(outerBlock, falseHead, predecessor))
                    .ToList();
                if (truePreds.Intersect(falsePreds).Any() ||
                    truePreds.Count + falsePreds.Count != froms.Count ||
                    !((truePreds.Count == 2 && falsePreds.Count == 1) ||
                      (truePreds.Count == 1 && falsePreds.Count == 2)))
                {
                    continue;
                }

                var nestedIsTrue = truePreds.Count == 2;
                var nestedPreds = nestedIsTrue ? truePreds : falsePreds;
                var directPred = (nestedIsTrue ? falsePreds : truePreds)[0];
                var nestedHead = nestedIsTrue ? trueHead : falseHead;
                var innerBlock = conditionCandidates
                    .Where(block => block != outerBlock && CanReach(nestedHead, block))
                    .OrderBy(block => block.Start)
                    .FirstOrDefault(block =>
                    {
                        var condition = block.Statements.GetCondition();
                        var innerTrue = block.To.FirstOrDefault(next => next.Start == condition.TrueBranch);
                        var innerFalse = block.To.FirstOrDefault(next => next.Start == condition.FalseBranch);
                        return innerTrue != null && innerFalse != null &&
                               nestedPreds.Count(predecessor =>
                                   BranchReaches(block, innerTrue, predecessor)) == 1 &&
                               nestedPreds.Count(predecessor =>
                                   BranchReaches(block, innerFalse, predecessor)) == 1;
                    });
                if (innerBlock == null)
                {
                    continue;
                }

                var innerCondition = innerBlock.Statements.GetCondition();
                var innerTrueHead = innerBlock.To.First(block => block.Start == innerCondition.TrueBranch);
                var innerFalseHead = innerBlock.To.First(block => block.Start == innerCondition.FalseBranch);
                var innerTruePred = nestedPreds.Single(predecessor =>
                    BranchReaches(innerBlock, innerTrueHead, predecessor));
                var innerFalsePred = nestedPreds.Single(predecessor =>
                    BranchReaches(innerBlock, innerFalseHead, predecessor));
                var nestedPhi = new PhiExpression(phi.Slot)
                {
                    Condition = new ConditionExpression(innerCondition.Condition, innerCondition.JumpIf),
                    ThenBranch = values[innerTruePred],
                    ElseBranch = values[innerFalsePred]
                };
                nestedPhi.PossibleExpressions.Add(values[innerTruePred]);
                nestedPhi.PossibleExpressions.Add(values[innerFalsePred]);

                phi.Condition = new ConditionExpression(outerCondition.Condition, outerCondition.JumpIf);
                phi.ThenBranch = nestedIsTrue ? nestedPhi : values[directPred];
                phi.ElseBranch = nestedIsTrue ? values[directPred] : nestedPhi;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 从只含条件和空跳转块的决策链反推出“到达某个取值前驱”的谓词。
        /// 这同时覆盖 a || b、a && b 以及多个 case 共用同一取值的三元表达式。
        /// </summary>
        private static bool TryAnnotateDecisionChainPhi(
            Block mergeBlock, List<Block> froms, PhiExpression phi)
        {
            var target = froms.OrderBy(block => block.Start).First();
            var other = froms.First(block => block != target);
            var candidates = new HashSet<Block>();
            var pending = new Queue<(Block Block, int Depth)>();
            foreach (var predecessor in froms)
            {
                pending.Enqueue((predecessor, 0));
            }

            while (pending.Count > 0)
            {
                var (current, depth) = pending.Dequeue();
                if (depth > 6)
                {
                    continue;
                }

                if (current.Statements?.GetCondition() != null)
                {
                    candidates.Add(current);
                }

                foreach (var previous in current.From)
                {
                    pending.Enqueue((previous, depth + 1));
                }
            }

            (bool? Constant, Expression Expression)? Combine(
                Expression condition,
                (bool? Constant, Expression Expression) whenTrue,
                (bool? Constant, Expression Expression) whenFalse)
            {
                if (whenTrue.Constant == true && whenFalse.Constant == false)
                    return (null, condition);
                if (whenTrue.Constant == false && whenFalse.Constant == true)
                    return (null, condition.Invert());
                if (whenTrue.Constant == true && whenFalse.Expression != null)
                    return (null, condition.Or(whenFalse.Expression));
                if (whenTrue.Expression != null && whenFalse.Constant == true)
                    return (null, condition.Invert().Or(whenTrue.Expression));
                if (whenTrue.Expression != null && whenFalse.Constant == false)
                    return (null, condition.And(whenTrue.Expression));
                if (whenTrue.Constant == false && whenFalse.Expression != null)
                    return (null, condition.Invert().And(whenFalse.Expression));
                if (whenTrue.Constant == whenFalse.Constant && whenTrue.Constant != null)
                    return (whenTrue.Constant, null);
                if (whenTrue.Expression != null && whenFalse.Expression != null)
                {
                    if (whenTrue.Expression.ToString() == whenFalse.Expression.ToString())
                        return (null, whenTrue.Expression);
                    return (null, condition.And(whenTrue.Expression)
                        .Or(condition.Invert().And(whenFalse.Expression)));
                }

                return null;
            }

            foreach (var root in candidates.OrderBy(block => block.Start))
            {
                var memo = new Dictionary<Block, (bool? Constant, Expression Expression)?>();
                var visiting = new HashSet<Block>();
                (bool? Constant, Expression Expression)? Build(Block current)
                {
                    if (current == target)
                        return (true, null);
                    if (current == other)
                        return (false, null);
                    if (current == null || current == mergeBlock || !visiting.Add(current))
                        return null;
                    if (memo.TryGetValue(current, out var cached))
                    {
                        visiting.Remove(current);
                        return cached;
                    }

                    (bool? Constant, Expression Expression)? result = null;
                    var condition = current.Statements?.GetCondition();
                    if (condition != null && current.To.Count == 2)
                    {
                        var trueHead = current.To.FirstOrDefault(block =>
                            block.Start == condition.TrueBranch);
                        var falseHead = current.To.FirstOrDefault(block =>
                            block.Start == condition.FalseBranch);
                        var whenTrue = Build(trueHead);
                        var whenFalse = Build(falseHead);
                        if (whenTrue != null && whenFalse != null)
                        {
                            result = Combine(condition.Condition, whenTrue.Value, whenFalse.Value);
                        }
                    }
                    else if (current.To.Count == 1 &&
                             (current.Statements == null ||
                              current.Statements.All(statement => statement is GotoExpression)))
                    {
                        result = Build(current.To[0]);
                    }

                    visiting.Remove(current);
                    memo[current] = result;
                    return result;
                }

                var predicate = Build(root);
                if (predicate?.Expression == null || predicate.Value.Constant != null)
                {
                    continue;
                }

                var targetIndex = froms.IndexOf(target);
                var otherIndex = froms.IndexOf(other);
                phi.Condition = new ConditionExpression(predicate.Value.Expression, false);
                phi.ThenBranch = phi.PossibleExpressions[targetIndex];
                phi.ElseBranch = phi.PossibleExpressions[otherIndex];
                return true;
            }

            return false;
        }
    }
}
