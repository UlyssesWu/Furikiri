using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private static bool TryAnnotateDecisionGraphPhi(
            Block mergeBlock, List<Block> froms, PhiExpression phi)
        {
            var values = new Dictionary<Block, Expression>();
            for (var index = 0; index < froms.Count; index++)
            {
                var predecessor = froms[index];
                var value = phi.PossibleExpressions[index];
                if (values.TryGetValue(predecessor, out var existing))
                {
                    // 条件跳转的真假边可能同时落到同一基本块，使 From 中出现重复项。
                    // 重复边只能合并同一个块末状态；若值不一致，说明对应关系不可靠。
                    if (!ExpressionStructuralComparer.AreEquivalent(existing, value))
                    {
                        return false;
                    }

                    continue;
                }

                values.Add(predecessor, value);
            }
            var candidates = new HashSet<Block>();
            var backward = new Queue<(Block Block, int Depth)>();
            var backwardVisited = new HashSet<Block>();
            foreach (var predecessor in froms)
            {
                backward.Enqueue((predecessor, 0));
            }

            while (backward.Count > 0)
            {
                var (current, depth) = backward.Dequeue();
                if (depth > 12 || !backwardVisited.Add(current))
                {
                    continue;
                }

                if (current.Statements?.GetCondition() != null)
                {
                    candidates.Add(current);
                }

                foreach (var previous in current.From)
                {
                    backward.Enqueue((previous, depth + 1));
                }
            }

            bool DominatesMerge(Block candidate)
            {
                // 从 merge 的每个入口反向搜索；遇到 candidate 就截断该路径。
                // 若还能到达 CFG 入口，说明存在绕过 candidate 的求值路径，
                // 以它为根构造出的表达式必然会遗漏那条路径上的守卫。
                var reverse = new Stack<Block>(froms.Distinct());
                var visited = new HashSet<Block>();
                while (reverse.Count > 0)
                {
                    var current = reverse.Pop();
                    if (current == candidate || !visited.Add(current))
                    {
                        continue;
                    }

                    if (current.From.Count == 0)
                    {
                        return false;
                    }

                    foreach (var previous in current.From)
                    {
                        reverse.Push(previous);
                    }
                }

                return true;
            }

            foreach (var root in candidates.OrderByDescending(block => block.Start))
            {
                if (!DominatesMerge(root))
                {
                    continue;
                }

                var visiting = new HashSet<Block>();
                // A decision graph may reconverge after every test. Rebuilding the
                // shared suffix for both edges turns a linear CFG into an exponential
                // expression tree, even when its arms ultimately select the same value.
                var built = new Dictionary<Block, (Expression Expression, HashSet<Block> Used)>();
                (Expression Expression, HashSet<Block> Used)? Build(Block current)
                {
                    if (current != null && built.TryGetValue(current, out var cached))
                    {
                        return cached;
                    }
                    if (current == null || current == mergeBlock || !visiting.Add(current))
                    {
                        return null;
                    }

                    (Expression Expression, HashSet<Block> Used)? result = null;
                    var condition = current.Statements?.GetCondition();
                    if (condition != null && current.To.Count == 2)
                    {
                        (Expression Expression, HashSet<Block> Used)? BuildBranch(int branchStart)
                        {
                            var head = current.To.FirstOrDefault(block => block.Start == branchStart);
                            if (head == mergeBlock)
                            {
                                return values.TryGetValue(current, out var directValue)
                                    ? (directValue, new HashSet<Block> { current })
                                    : null;
                            }

                            return Build(head);
                        }

                        var whenTrue = BuildBranch(condition.TrueBranch);
                        var whenFalse = BuildBranch(condition.FalseBranch);
                        if (whenTrue != null && whenFalse != null)
                        {
                            var used = new HashSet<Block>(whenTrue.Value.Used);
                            used.UnionWith(whenFalse.Value.Used);
                            if (ExpressionStructuralComparer.AreEquivalent(
                                whenTrue.Value.Expression, whenFalse.Value.Expression))
                            {
                                // 两条决策路径最终携带同一个值时，当前条件对结果没有影响。
                                // 直接折叠可避免把公共的后续布尔值重复展开成多层三元表达式。
                                result = (whenTrue.Value.Expression, used);
                            }
                            else
                            {
                                var nested = new PhiExpression(phi.Slot)
                                {
                                    Condition = new ConditionExpression(
                                        condition.Condition, condition.JumpIf),
                                    ThenBranch = whenTrue.Value.Expression,
                                    ElseBranch = whenFalse.Value.Expression
                                };
                                nested.PossibleExpressions.Add(whenTrue.Value.Expression);
                                nested.PossibleExpressions.Add(whenFalse.Value.Expression);
                                result = (nested, used);
                            }
                        }
                    }
                    else if (values.TryGetValue(current, out var value))
                    {
                        result = (value, new HashSet<Block> { current });
                    }
                    else if (current.To.Count == 1 &&
                             (current.Statements == null ||
                              current.Statements.All(statement => statement is GotoExpression)))
                    {
                        result = Build(current.To[0]);
                    }

                    visiting.Remove(current);
                    if (result.HasValue)
                    {
                        built[current] = result.Value;
                    }
                    return result;
                }

                var resolved = Build(root);
                if (resolved?.Expression is not PhiExpression nestedPhi ||
                    !nestedPhi.IsConditional || !resolved.Value.Used.SetEquals(froms))
                {
                    continue;
                }

                phi.Condition = nestedPhi.Condition;
                phi.ThenBranch = nestedPhi.ThenBranch;
                phi.ElseBranch = nestedPhi.ElseBranch;
                return true;
            }

            return false;
        }
    }
}
