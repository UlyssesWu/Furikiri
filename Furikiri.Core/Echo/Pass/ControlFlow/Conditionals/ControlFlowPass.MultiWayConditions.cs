using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        private bool TryStructureMultiWayConditionChain(
            Block root, HashSet<Block> conditionBlocks, List<Block> terminals,
            HashSet<Block> passthroughBlocks, out IfLogic logic)
        {
            logic = null;
            var postDominator = FindIfPostDominator(root);
            if (postDominator == null)
            {
                return false;
            }

            // 提前 return 会使图论上的共同后支配点落到函数出口，但源码中的
            // `if (outer) { if (guard) return; update(); } continueWork();`
            // 仍有一个正常流汇合点。若某个决策出口可由其余正常出口到达，而
            // 不能到达它的出口都直接终止函数，就把它当作本条件链的公共后继。
            // 这样 continueWork 会留在 if 外，而不是被误塞进最后一个 else。
            Block normalContinuation = null;
            // Stop at the original join: a path through that join and then to
            // return is ordinary continuation, not an early terminating arm.
            if (!_context.LoopSet.Any(loop => loop.Contains(root)))
            {
                normalContinuation = terminals
                    .Where(candidate => terminals.Any(other =>
                        other != candidate && CanReach(other, candidate)))
                    .Where(candidate => terminals.All(other =>
                        other == candidate || CanReach(other, candidate) ||
                        AllPathsTerminateBefore(other, postDominator)))
                    .OrderBy(candidate => candidate.Start)
                    .FirstOrDefault();
                if (normalContinuation != null)
                {
                    postDominator = normalContinuation;
                }
            }
            else
            {
                var loop = _context.LoopSet.Where(candidate => candidate.Contains(root))
                    .OrderBy(candidate => candidate.Blocks.Count).First();
                normalContinuation = terminals
                    .Where(candidate => candidate != loop.Header &&
                        !IsContinueTarget(candidate, loop) &&
                        terminals.Any(other => other != candidate &&
                            CanReachWithoutLoopBackEdge(other, candidate, loop.Header)))
                    .Where(candidate => terminals.All(other => other == candidate ||
                        AllPathsReachOrTerminateAt(other, candidate, loop)))
                    .OrderBy(candidate => candidate.Start).FirstOrDefault();
                if (normalContinuation != null)
                    postDominator = normalContinuation;
            }

            var rootCondition = root.Statements.GetCondition();
            if (rootCondition != null &&
                (_context.BlockTable[rootCondition.TrueBranch] == postDominator ||
                 _context.BlockTable[rootCondition.FalseBranch] == postDominator))
            {
                // 根条件的一侧直接结束、另一侧才进入多路分派时，根是外围 gate。
                // 把它和内层 else-if 拉平会使后续分支在 gate=false 时也执行。
                if (normalContinuation == null)
                {
                    return false;
                }

                var trueTarget = _context.BlockTable[rootCondition.TrueBranch];
                var falseTarget = _context.BlockTable[rootCondition.FalseBranch];
                var gatedTarget = trueTarget == postDominator ? falseTarget : trueTarget;
                var gatedCondition = gatedTarget.Statements.GetCondition();
                if (gatedCondition != null &&
                    StructureIfElse(gatedTarget, out var gatedLogic))
                {
                    gatedTarget.Statements.Replace(
                        gatedCondition, gatedLogic.Simplify().ToStatement());
                }

                // The entry guard may finish before the rest of the gated
                // region. Materialize every remaining decision before taking
                // the outer body snapshot.
                var initialGatedRegion = CollectDominatedBranchRegion(
                    root, gatedTarget, postDominator, true);
                StructureNestedConditionChains(initialGatedRegion);
                foreach (var candidate in initialGatedRegion.OrderByDescending(block => block.Start))
                {
                    var nestedCondition = candidate.Hidden ? null : candidate.Statements.GetCondition();
                    if (nestedCondition != null && StructureIfElse(candidate, out var nestedLogic))
                        candidate.Statements.Replace(nestedCondition, nestedLogic.Simplify().ToStatement());
                }

                var gatedRegion = CollectDominatedBranchRegion(
                    root, gatedTarget, postDominator, true);
                if (gatedRegion.Count == 0)
                {
                    gatedRegion = new List<Block> { gatedTarget };
                }

                logic = new IfLogic
                {
                    ConditionBlock = root,
                    Condition = trueTarget == postDominator
                        ? rootCondition.Condition.Invert()
                        : rootCondition.Condition,
                    PostDominator = postDominator,
                    Then = { Type = LogicalBlockType.BlockList, Blocks = gatedRegion },
                    Else = { Type = LogicalBlockType.None }
                };
                foreach (var passthroughBlock in passthroughBlocks)
                {
                    passthroughBlock.Hidden = true;
                }

                return true;
            }

            // `if (A) { if (B) X; } else { if (C) Y; }` 也会形成三个终点，
            // 但它不是一条可安全拉平的 else-if 链：B=false 和 C=false 都会
            // 各自直达公共后继。若把 C 只按自己的局部谓词输出成 `else if (C)`，
            // A=true、B=false 时也会错误执行 C。保留这种分叉树，交给普通的
            // 递归 if/else 恢复，才能保住外层互斥门控。
            var directContinuationDecisions = conditionBlocks.Count(candidate =>
            {
                var condition = candidate.Statements.GetCondition();
                if (condition == null)
                {
                    return false;
                }

                var trueTarget = NormalizeConditionChainTarget(
                    _context.BlockTable[condition.TrueBranch], passthroughBlocks);
                var falseTarget = NormalizeConditionChainTarget(
                    _context.BlockTable[condition.FalseBranch], passthroughBlocks);
                return trueTarget == postDominator || falseTarget == postDominator;
            });
            if (directContinuationDecisions > 1)
            {
                return false;
            }

            // 正常流汇合块在字节码中不保证排在各条件分支之后。构造 else-if
            // 时必须显式把它放到最后作为 default；否则会先输出公共后继，再把
            // 本应受外层条件控制的内层分支提升到后面。
            var orderedTerminals = terminals
                .Where(target => target != postDominator)
                .OrderBy(target => target.Start)
                .Concat(terminals.Where(target => target == postDominator))
                .ToList();
            var initialRegions = orderedTerminals.ToDictionary(
                target => target,
                target => target == postDominator
                    ? new List<Block>()
                    : CollectDominatedBranchRegion(root, target, postDominator, true));
            if (initialRegions.Any(pair => pair.Key != postDominator && pair.Value.Count == 0))
            {
                return false;
            }

            // Sequential branches can share a tail before the outer join.
            // Flattening them into mutually exclusive arms would run that tail
            // on only one path. Leave overlapping regions to nested recovery.
            if (initialRegions.Values.SelectMany(region => region)
                .GroupBy(block => block).Any(group => group.Count() > 1))
            {
                return false;
            }

            StructureNestedConditionChains(
                initialRegions.Values.SelectMany(region => region), conditionBlocks);

            foreach (var candidate in initialRegions.Values.SelectMany(region => region)
                         .Distinct()
                         .Where(candidate => !conditionBlocks.Contains(candidate))
                         .OrderByDescending(candidate => candidate.Start))
            {
                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition != null && StructureIfElse(candidate, out var nestedLogic))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedLogic.Simplify().ToStatement());
                }
            }

            var regions = orderedTerminals.ToDictionary(
                target => target,
                target => target == postDominator
                    ? new List<Block>()
                    : CollectDominatedBranchRegion(root, target, postDominator, true));
            if (regions.Any(pair => pair.Key != postDominator && pair.Value.Count == 0))
            {
                return false;
            }

            IfLogic nested = null;
            var defaultTarget = orderedTerminals[^1];
            for (var index = orderedTerminals.Count - 2; index >= 0; index--)
            {
                var target = orderedTerminals[index];
                var remainingTerminals = orderedTerminals.Skip(index).ToHashSet();
                var decisionRoot = conditionBlocks
                    .OrderBy(candidate => candidate.Start)
                    .FirstOrDefault(candidate => GetReachableDecisionTerminals(
                        candidate, terminals, passthroughBlocks).SetEquals(remainingTerminals))
                    ?? root;
                var predicate = BuildPathPredicate(
                    decisionRoot, target, terminals, conditionBlocks, passthroughBlocks);
                if (predicate?.Expression == null || predicate.Constant != null)
                {
                    return false;
                }

                var current = new IfLogic
                {
                    ConditionBlock = decisionRoot,
                    Condition = predicate.Expression,
                    PostDominator = postDominator,
                    Then = { Type = LogicalBlockType.BlockList, Blocks = regions[target] }
                };

                if (nested == null)
                {
                    current.Else.Type = defaultTarget == postDominator
                        ? LogicalBlockType.None
                        : LogicalBlockType.BlockList;
                    current.Else.Blocks = regions[defaultTarget];
                }
                else
                {
                    current.Else.Type = LogicalBlockType.Logical;
                    current.Else.Logic = nested;
                    nested.ParentIf = current;
                }

                nested = current;
            }

            HoistRepeatedInvocationComparisonTarget(root, conditionBlocks);
            foreach (var conditionBlock in conditionBlocks.Where(block => block != root))
            {
                conditionBlock.Hidden = true;
            }
            foreach (var passthroughBlock in passthroughBlocks)
            {
                passthroughBlock.Hidden = true;
            }

            logic = nested;
            return logic != null;
        }

        /// <summary>
        /// `if (A) { if (B) sideEffect(); if (C) body(); }` 中 B 的一条边
        /// 会先经过带载荷的基本块，再回到另一条边所指的 C。这是
        /// 两个顺序守卫，不是 `B &amp;&amp; C` 短路链。若把 B 并入外层谓词，
        /// C 的调用条件和其后的 return 区域都会被提升为无条件语句。
        /// </summary>
        private bool HasPayloadArmJoiningSibling(Block conditionBlock)
        {
            var condition = conditionBlock?.Statements?.GetCondition();
            if (condition == null || _context?.BlockTable == null ||
                !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
            {
                return false;
            }

            trueTarget = NormalizeConsumedConditionalPhiTarget(trueTarget);
            falseTarget = NormalizeConsumedConditionalPhiTarget(falseTarget);
            return trueTarget != falseTarget &&
                   (falseTarget.Statements.GetCondition() != null &&
                        HasBranchPayload(trueTarget) && trueTarget.To.Contains(falseTarget) ||
                    trueTarget.Statements.GetCondition() != null &&
                        HasBranchPayload(falseTarget) && falseTarget.To.Contains(trueTarget));
        }

        private HashSet<Block> GetReachableDecisionTerminals(
            Block start, IReadOnlyCollection<Block> terminals,
            HashSet<Block> passthroughBlocks)
        {
            var result = new HashSet<Block>();
            var visited = new HashSet<Block>();
            var pending = new Stack<Block>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = NormalizeConditionChainTarget(pending.Pop(), passthroughBlocks);
                if (!visited.Add(current))
                {
                    continue;
                }

                if (terminals.Contains(current))
                {
                    result.Add(current);
                    continue;
                }

                var condition = current.Statements.GetCondition();
                if (condition == null)
                {
                    continue;
                }

                pending.Push(_context.BlockTable[condition.TrueBranch]);
                pending.Push(_context.BlockTable[condition.FalseBranch]);
            }

            return result;
        }

        private PathPredicate BuildPathPredicate(
            Block root, Block bodyTarget, IReadOnlyCollection<Block> terminals,
            HashSet<Block> conditionBlocks, HashSet<Block> passthroughBlocks)
        {
            var memo = new Dictionary<Block, PathPredicate>();
            PathPredicate Build(Block current)
            {
                current = NormalizeConditionChainTarget(current, passthroughBlocks);
                if (terminals.Contains(current))
                {
                    return current == bodyTarget
                        ? PathPredicate.True()
                        : PathPredicate.False();
                }

                if (!conditionBlocks.Contains(current))
                {
                    return null;
                }

                if (memo.TryGetValue(current, out var cached))
                {
                    return cached;
                }

                var condition = current.Statements.GetCondition();
                var whenTrue = Build(_context.BlockTable[condition.TrueBranch]);
                var whenFalse = Build(_context.BlockTable[condition.FalseBranch]);
                if (whenTrue == null || whenFalse == null)
                {
                    return null;
                }

                var combined = PathPredicate.Combine(condition.Condition, whenTrue, whenFalse);
                memo[current] = combined;
                return combined;
            }

            return Build(root);
        }
    }
}
