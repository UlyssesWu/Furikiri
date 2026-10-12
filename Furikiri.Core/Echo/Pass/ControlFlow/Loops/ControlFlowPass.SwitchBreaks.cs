using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        /// <summary>
        /// 判断当前条件是否已经位于一个更外层的“本轮到此结束”分支区域中。
        /// 外层条件必须有一条边直接进入同一 continue 目标，且另一条边最终也只会
        /// 到达该目标或终止函数；这样可由最高层条件统一输出一次 continue。
        /// </summary>
        private bool HasOuterIterationCompletingOwner(
            Block root, Block continuationTarget, Loop loop)
        {
            if (root?.Dominator == null || continuationTarget == null || loop == null)
            {
                return false;
            }

            return loop.Blocks.Any(candidate =>
                candidate != root && !candidate.Hidden &&
                root.Dominator[candidate.Id] &&
                candidate.Statements.GetCondition() != null &&
                candidate.To.Contains(continuationTarget) &&
                candidate.To.All(target =>
                    AllPathsReachOrTerminateAt(target, continuationTarget)));
        }

        /// <summary>
        /// 判断循环外分支是否只会到达该循环的退出块（或提前 return/throw）。
        /// 与普通“到达公共尾部”判定不同，重新进入循环头或闩锁不能算成功；
        /// 否则带正文的 break 分支会被误认成 continue 分支。
        /// </summary>
        private bool AllPathsReachLoopExitOrTerminate(Block start, Block loopExit, Loop loop)
        {
            if (start == null || loopExit == null || loop == null ||
                !CanReach(start, loopExit))
            {
                return false;
            }

            var memo = new Dictionary<Block, bool>();
            var visiting = new HashSet<Block>();
            bool Visit(Block current)
            {
                if (current == loopExit)
                {
                    return true;
                }

                if (current == null || loop.Contains(current) ||
                    IsContinueTarget(current, loop))
                {
                    return false;
                }

                if (current.Statements.Any(statement =>
                        statement is ReturnExpression or ThrowExpression))
                {
                    return true;
                }

                if (memo.TryGetValue(current, out var cached))
                {
                    return cached;
                }

                if (!visiting.Add(current) || current.To.Count == 0)
                {
                    return false;
                }

                var nestedLoop = _context.LoopSet.FirstOrDefault(candidate =>
                    candidate != loop && candidate.Header == current && candidate.MaterializedStatement != null);
                // A materialized loop is one statement. Its backedges do not
                // return to the surrounding loop; inspect its exits instead.
                var successors = nestedLoop == null ? current.To : nestedLoop.Blocks
                    .SelectMany(block => block.To).Where(target => !nestedLoop.Contains(target))
                    .Distinct().ToList();
                var result = successors.Count > 0 && successors.All(Visit);
                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Visit(start);
        }

        /// <summary>
        /// 恢复循环中“首个空 case break、其余 case continue”的分派。
        /// 空 case 的目标可能同时由分派外的路径进入，因此不受当前比较块支配；
        /// 若把它收入 case 体，共享尾部就会只在外层某一分支执行。这里改写为
        /// `if (!emptyCase) { ...; continue; }`，让 break 路径自然落入公共尾部。
        /// </summary>
        private bool TryStructureLoopSwitchBreakGuard(
            Block root, ConditionExpression condition, out IfLogic logic)
        {
            logic = null;
            if (condition?.Condition is not BinaryExpression comparison ||
                comparison.Op is not (BinaryOp.Equal or BinaryOp.Congruent))
            {
                return false;
            }

            var loop = _context.LoopSet
                .Where(candidate => candidate.Contains(root))
                .OrderBy(candidate => candidate.Blocks.Count)
                .FirstOrDefault();
            if (loop == null ||
                !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
            {
                return false;
            }

            // 该图形本身来自完整 equality 分派时，保留 case/default 的控制层
            // 所有权，不再降级成反向 if。空 case 的 break 自然进入共享尾部，
            // 其他 case/default 的 continue 则仍退出当前外层循环迭代。
            if (TryStructureLoopSwitchBreakDispatch(root, loop, out logic))
            {
                return true;
            }

            var rawTrueTarget = trueTarget;
            var rawFalseTarget = falseTarget;
            var passthroughBlocks = new HashSet<Block>();
            trueTarget = NormalizeDecisionTarget(trueTarget, passthroughBlocks);
            falseTarget = NormalizeDecisionTarget(falseTarget, passthroughBlocks);

            bool IsSharedContinuation(Block target)
            {
                // 这里只处理 switch 的 break 后仍留在当前循环体内的公共尾部。
                // 若目标已在循环外，它是循环本身的 break，必须交给普通分支恢复
                // 显式输出；否则 while 正文会丢失退出语句并成为死循环。
                return target != null && loop.Contains(target) && target != loop.Header &&
                       !IsContinueTarget(target, loop) &&
                       (target.Dominator == null || !target.Dominator[root.Id]);
            }

            var trueIsShared = IsSharedContinuation(trueTarget);
            var falseIsShared = IsSharedContinuation(falseTarget);
            if (trueIsShared == falseIsShared)
            {
                return false;
            }

            var sharedTarget = trueIsShared ? trueTarget : falseTarget;
            var continueEntry = trueIsShared ? falseTarget : trueTarget;
            var sharedRawTarget = trueIsShared ? rawTrueTarget : rawFalseTarget;
            var continueCondition = continueEntry.Statements.GetCondition();
            if (!IsDecisionPassthrough(sharedRawTarget) ||
                (continueCondition?.Condition is BinaryExpression nextComparison &&
                 (nextComparison.Op is not (BinaryOp.Equal or BinaryOp.Congruent) ||
                  nextComparison.Left?.ToString() != comparison.Left?.ToString())))
            {
                return false;
            }

            // This rewrite appends an unconditional continue. A normal if arm
            // that can fall through to the shared tail must retain that path.
            if (CanReachWithoutLoopBackEdge(continueEntry, sharedTarget, loop.Header) ||
                !AllPathsReachOrTerminateAt(continueEntry, sharedTarget, loop))
            {
                return false;
            }

            var initialRegion = CollectDominatedBranchRegion(
                root, continueEntry, sharedTarget, true);
            if (initialRegion.Count == 0)
            {
                return false;
            }

            StructureNestedConditionChains(initialRegion);
            foreach (var candidate in initialRegion.OrderByDescending(candidate => candidate.Start))
            {
                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition != null && StructureIfElse(candidate, out var nestedLogic))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedLogic.Simplify().ToStatement());
                }
            }

            var visibleRegion = CollectDominatedBranchRegion(
                root, continueEntry, sharedTarget, true);
            if (visibleRegion.Count == 0)
            {
                return false;
            }

            var guardedBody = new BlockStatement(visibleRegion, true);
            guardedBody.Statements.Add(new ContinueStatement());
            visibleRegion.SafeHide();
            foreach (var passthroughBlock in passthroughBlocks)
            {
                passthroughBlock.Hidden = true;
            }

            logic = new IfLogic
            {
                ConditionBlock = root,
                Condition = trueIsShared ? condition.Condition.Invert() : condition.Condition,
                PostDominator = sharedTarget,
                Then =
                {
                    Type = LogicalBlockType.Statement,
                    Statement = guardedBody
                },
                Else = { Type = LogicalBlockType.None }
            };
            return true;
        }

        private bool TryStructureLoopSwitchBreakDispatch(
            Block root, Loop loop, out IfLogic logic)
        {
            logic = null;
            if (!EqualityDispatchAnalyzer.TryAnalyze(
                    _context, root, NormalizeEqualityDispatchTarget,
                    out var dispatch) ||
                dispatch.ConditionBlocks.Count < 2 ||
                !IsContinueTarget(dispatch.DefaultTarget, loop))
            {
                return false;
            }

            var sharedGroup = dispatch.Groups.FirstOrDefault(group =>
                group.Target != loop.Header && loop.Contains(group.Target) &&
                !IsContinueTarget(group.Target, loop) &&
                (group.Target.Dominator == null ||
                 !group.Target.Dominator[root.Id]));
            if (sharedGroup == null || dispatch.Groups[0] != sharedGroup)
            {
                return false;
            }

            var bodies = new Dictionary<EqualityDispatchGroup, Statement>();
            foreach (var group in dispatch.Groups)
            {
                if (group == sharedGroup)
                {
                    bodies[group] = null;
                }
                else if (IsContinueTarget(group.Target, loop))
                {
                    bodies[group] = new ContinueStatement();
                }
                else if (IsContinueArmWithPayload(group.Target, loop))
                {
                    bodies[group] = MoveLoopContinueArmToStatement(group.Target);
                }
                else
                {
                    return false;
                }
            }

            IfLogic nested = null;
            for (var index = dispatch.Groups.Count - 1; index >= 0; index--)
            {
                var group = dispatch.Groups[index];
                var combinedCondition = group.Conditions[0];
                for (var labelIndex = 1;
                     labelIndex < group.Conditions.Count; labelIndex++)
                {
                    combinedCondition = combinedCondition.Or(
                        group.Conditions[labelIndex]);
                }

                var current = new IfLogic
                {
                    IsEqualityDispatch = true,
                    HasCompilerSwitchEvidence = true,
                    ConditionBlock = group.Owner,
                    Condition = combinedCondition,
                    PostDominator = sharedGroup.Target,
                    Then =
                    {
                        Type = bodies[group] == null
                            ? LogicalBlockType.None
                            : LogicalBlockType.Statement,
                        Statement = bodies[group]
                    }
                };
                if (nested == null)
                {
                    current.Else.Type = LogicalBlockType.Statement;
                    current.Else.Statement = new ContinueStatement();
                }
                else
                {
                    current.Else.Type = LogicalBlockType.Logical;
                    current.Else.Logic = nested;
                    nested.ParentIf = current;
                }

                nested = current;
            }

            foreach (var conditionBlock in dispatch.ConditionBlocks
                         .Where(block => block != root))
            {
                conditionBlock.Hidden = true;
            }
            foreach (var label in dispatch.Labels)
            {
                label.Hidden = true;
            }
            foreach (var group in dispatch.Groups.Where(group =>
                         group != sharedGroup && group.Target != loop.Header))
            {
                group.Target.Hidden = true;
            }

            logic = nested;
            return logic != null;
        }
    }
}
