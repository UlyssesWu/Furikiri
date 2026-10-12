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
        /// 恢复循环中的两段短路守卫：第一、第二个失败分支都跳到同一公共尾部，
        /// 只有两项均命中才执行载荷。公共尾部自身常是三元表达式或循环闩锁，
        /// 通用条件链容易继续穿入其中并最终只留下裸比较。
        /// </summary>
        private void StructureTwoStageGuardedPayloads(IEnumerable<Block> candidates)
        {
            if (_context?.BlockTable == null)
            {
                return;
            }

            foreach (var root in candidates
                         .Where(block => !block.Hidden &&
                                         block.Statements.GetCondition() != null)
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                var rootCondition = root.Statements.GetCondition();
                var rootTrue = _context.BlockTable[rootCondition.TrueBranch];
                var rootFalse = _context.BlockTable[rootCondition.FalseBranch];
                var next = rootTrue.Statements.IsCondition() ? rootTrue :
                    rootFalse.Statements.IsCondition() ? rootFalse : null;
                var skip = next == rootTrue ? rootFalse : rootTrue;
                var nextCondition = next?.Statements.GetCondition();
                if (nextCondition == null)
                {
                    continue;
                }

                var nextTrue = _context.BlockTable[nextCondition.TrueBranch];
                var nextFalse = _context.BlockTable[nextCondition.FalseBranch];
                Block body;
                if (nextTrue == skip)
                {
                    body = nextFalse;
                }
                else if (nextFalse == skip)
                {
                    body = nextTrue;
                }
                else
                {
                    continue;
                }

                // skip 若还能只经过条件/跳板到达同一个载荷，它不是守卫失败后的
                // 公共尾部，而是 `(A && B) || (C && D)` 的另一条成功路径。
                // 此时局部认领前两项会隐藏完整判定 DAG 的入口，并丢掉 C/D 中的
                // 调用等副作用；应留给通用条件链一次性计算到达 body 的谓词。
                if (CanReachThroughConditionBlocks(skip, body))
                {
                    continue;
                }

                if (!HasBranchPayload(body))
                {
                    continue;
                }

                var containingLoop = _context.LoopSet
                    .Where(loop => loop.Contains(root))
                    .OrderBy(loop => loop.Blocks.Count)
                    .FirstOrDefault();
                var loopBreak = containingLoop?.Break ??
                                (containingLoop?.LoopLogic as DoWhileLogic)?.Break ??
                                (containingLoop == null ? null : FindBreak(containingLoop));

                var enterNext = next == rootTrue
                    ? rootCondition.Condition
                    : rootCondition.Condition.Invert();
                var enterBody = body == nextTrue
                    ? nextCondition.Condition
                    : nextCondition.Condition.Invert();

                // 自然循环只包含能回到循环头的块；带副作用后直接跳到循环出口的
                // 载荷因此位于集合之外。它不能按“提前终止后也算到达 skip”收入
                // 普通守卫，否则原本的 break 跳转会在 AST 快照中消失。
                if (containingLoop != null && !containingLoop.Contains(body) &&
                    IsLoopBreakArm(body, loopBreak, containingLoop))
                {
                    var breakGuard = new IfLogic
                    {
                        ConditionBlock = root,
                        Condition = enterNext.And(enterBody),
                        PostDominator = skip,
                        Then =
                        {
                            Type = LogicalBlockType.Statement,
                            Statement = MoveLoopBreakArmToStatement(body, loopBreak)
                        },
                        Else = { Type = LogicalBlockType.None }
                    };
                    next.Hidden = true;
                    body.Hidden = true;
                    root.Statements.Replace(
                        rootCondition, breakGuard.Simplify().ToStatement());
                    continue;
                }

                // A failed guard leaving the current loop is a break, not an empty
                // branch. Let the loop-aware condition chain preserve that transfer.
                if (containingLoop != null && !containingLoop.Contains(skip))
                {
                    continue;
                }

                var bodyRegion = body.To.Contains(skip)
                    ? new List<Block> { body }
                    : CollectDominatedBranchRegion(root, body, skip, true);
                if (bodyRegion.Count == 0 ||
                    containingLoop != null &&
                    !CanReachWithoutLoopBackEdge(body, skip, containingLoop.Header) ||
                    !AllPathsReachOrTerminateAt(body, skip,
                        containingLoop))
                {
                    continue;
                }

                // 两项守卫之后的载荷可能横跨多个基本块，并含若干普通叶子 if。
                // 先由内向外恢复它们，再把整个支配区域放进合并后的守卫；否则
                // 外层循环会保存一份仍含裸 ConditionExpression 的旧快照。
                foreach (var nested in bodyRegion
                             .Where(block => block != root && block != next)
                             .OrderByDescending(block => block.Start)
                             .ToList())
                {
                    var nestedCondition = nested.Statements.GetCondition();
                    if (nestedCondition != null &&
                        StructureIfElse(nested, out var nestedLogic))
                    {
                        nested.Statements.Replace(
                            nestedCondition, nestedLogic.Simplify().ToStatement());
                    }
                }

                bodyRegion = CollectDominatedBranchRegion(root, body, skip, true);
                if (bodyRegion.Count == 0)
                {
                    bodyRegion = new List<Block> { body };
                }

                var recovered = new IfLogic
                {
                    ConditionBlock = root,
                    Condition = enterNext.And(enterBody),
                    PostDominator = skip,
                    Then = { Type = LogicalBlockType.BlockList, Blocks = bodyRegion },
                    Else = { Type = LogicalBlockType.None }
                };
                next.Hidden = true;
                RemoveLastGoto(bodyRegion.Last(), skip);
                root.Statements.Replace(
                    rootCondition, recovered.Simplify().ToStatement());
            }
        }

        /// <summary>
        /// 收集某个分支入口所支配的完整区域。编译器经常把一个源码分支拆成
        /// “准备语句 + 循环 + 返回值”多个基本块，只保留入口块会把后续语句
        /// 错误提升到 if 外。共享汇合块不受分支入口支配，因此会自然排除。
        /// </summary>
        private List<Block> CollectDominatedBranchRegion(
            Block conditionBlock, Block branchEntry, Block postDominator, bool visibleOnly)
        {
            if (conditionBlock == null || branchEntry == null || branchEntry == postDominator ||
                branchEntry.Dominator == null || !branchEntry.Dominator[conditionBlock.Id])
            {
                return new List<Block>();
            }

            var containingLoop = _context.LoopSet
                .Where(loop => loop.Contains(conditionBlock))
                .OrderBy(loop => loop.Blocks.Count)
                .FirstOrDefault();
            var branchStaysInLoop = containingLoop != null && containingLoop.Contains(branchEntry);
            var normalLoopExit = containingLoop?.Break ??
                                 (containingLoop?.LoopLogic as DoWhileLogic)?.Break ??
                                 (containingLoop == null ? null : FindBreak(containingLoop));

            return _context.Blocks
                .Where(candidate => candidate != conditionBlock &&
                                    candidate != postDominator &&
                                    candidate != _context.ExitBlock &&
                                    candidate.Dominator != null &&
                                    candidate.Dominator[conditionBlock.Id] &&
                                    candidate.Dominator[branchEntry.Id] &&
                                    (!branchStaysInLoop || normalLoopExit == null ||
                                     (candidate != normalLoopExit &&
                                      (candidate.Dominator == null ||
                                       !candidate.Dominator[normalLoopExit.Id]))) &&
                                    (!visibleOnly || !candidate.Hidden))
                .OrderBy(candidate => candidate.Start)
                .ToList();
        }

        /// <summary>
        /// 将只读区域计划中的分支所有权转换为当前 AST 阶段可消费的块列表。
        /// 计划负责从原始 CFG 排除公共继续块和两臂共享块；支配检查只作为迁移期
        /// 的安全边界，防止把存在区域外入口的块提前声明为当前 if 独占。
        /// </summary>
        private List<Block> CollectPlannedBranchRegion(
            Block conditionBlock, Block branchEntry,
            IReadOnlyList<Block> ownedBlocks, bool visibleOnly)
        {
            if (conditionBlock == null || branchEntry == null ||
                ownedBlocks == null || branchEntry.Dominator == null ||
                !branchEntry.Dominator[conditionBlock.Id])
            {
                return new List<Block>();
            }

            return ownedBlocks
                .Where(candidate => candidate != null &&
                                    candidate != conditionBlock &&
                                    candidate != _context.ExitBlock &&
                                    candidate.Dominator != null &&
                                    candidate.Dominator[conditionBlock.Id] &&
                                    candidate.Dominator[branchEntry.Id] &&
                                    (!visibleOnly || !candidate.Hidden))
                .OrderBy(candidate => candidate.Start)
                .ToList();
        }

        /// <summary>
        /// 多基本块分支使用支配区域直接结构化。区域内条件从后向前处理，
        /// 使内层 if 先隐藏自己的子块，随后外层区域只收集仍可见的语句块。
        /// </summary>
        private bool TryStructureDominatedBranchRegions(
            Block conditionBlock, Block thenEntry, Block elseEntry,
            Block postDominator, IfLogic logic, IfRegionPlan regionPlan = null)
        {
            var thenReturn = thenEntry?.Statements.OfType<ReturnExpression>().FirstOrDefault();
            var elseReturn = elseEntry?.Statements.OfType<ReturnExpression>().FirstOrDefault();
            var thenIsConstantReturn = thenReturn?.Return is ConstantExpression;
            var elseIsConstantReturn = elseReturn?.Return is ConstantExpression;

            // 条件块先更新局部值、随后一臂直接返回常量时，该臂是提前返回守卫，
            // 另一臂通常只是守卫后的公共后继。若按支配区域收集，会把公共后继
            // 包进显式 else。纯条件返回仍交给通用逻辑，避免破坏布尔/三元返回。
            var hasLeadingSelfAssignment = conditionBlock?.Statements.Any(statement =>
                statement is BinaryExpression binary && binary.Op.CanSelfAssign() &&
                binary.Left is LocalExpression) == true;
            if (hasLeadingSelfAssignment &&
                thenIsConstantReturn != elseIsConstantReturn)
            {
                logic.Condition = thenIsConstantReturn
                    ? logic.Condition
                    : logic.Condition.Invert();
                logic.Then.Type = LogicalBlockType.BlockList;
                logic.Then.Blocks = new List<Block>
                {
                    thenIsConstantReturn ? thenEntry : elseEntry
                };
                logic.Else.Type = LogicalBlockType.None;
                logic.Else.Blocks = new List<Block>();
                return true;
            }

            var containingLoop = _context.LoopSet
                .Where(loop => loop.Contains(conditionBlock))
                .OrderBy(loop => loop.Blocks.Count)
                .FirstOrDefault();
            var isLoopContinueSingleArmPlan = containingLoop != null &&
                postDominator == containingLoop.Header &&
                regionPlan != null &&
                ((thenEntry == containingLoop.Header &&
                  regionPlan.ThenBlocks.Count == 0 &&
                  regionPlan.ElseBlocks.Count > 0) ||
                 (elseEntry == containingLoop.Header &&
                  regionPlan.ElseBlocks.Count == 0 &&
                  regionPlan.ThenBlocks.Count > 0));
            var isLoopMixedSingleArmPlan = containingLoop != null &&
                postDominator != containingLoop.Header &&
                regionPlan != null &&
                ((regionPlan.ThenReachesElseOrTerminates &&
                  regionPlan.ThenBlocks.Count > 0 &&
                  regionPlan.ElseBlocks.Count == 0) ||
                 (regionPlan.ElseReachesThenOrTerminates &&
                  regionPlan.ElseBlocks.Count > 0 &&
                  regionPlan.ThenBlocks.Count == 0));
            var isSameIterationTwoArmPlan = containingLoop != null &&
                regionPlan?.UsesSameIterationContinuation == true &&
                regionPlan.ThenBlocks.Count > 0 &&
                regionPlan.ElseBlocks.Count > 0;
            var canUsePlan = regionPlan != null &&
                             regionPlan.NormalContinuation == postDominator &&
                             regionPlan.ThenEntry == thenEntry &&
                             regionPlan.ElseEntry == elseEntry &&
                             (containingLoop == null ||
                              isLoopContinueSingleArmPlan ||
                              isLoopMixedSingleArmPlan ||
                              isSameIterationTwoArmPlan);
            List<Block> CollectBranch(
                Block entry, IReadOnlyList<Block> plannedBlocks)
            {
                return canUsePlan
                    ? CollectPlannedBranchRegion(
                        conditionBlock, entry, plannedBlocks, true)
                    : CollectDominatedBranchRegion(
                        conditionBlock, entry, postDominator, true);
            }

            var initialThen = CollectBranch(thenEntry, regionPlan?.ThenBlocks);
            var initialElse = CollectBranch(elseEntry, regionPlan?.ElseBlocks);
            if (containingLoop != null && postDominator != containingLoop.Header &&
                !IsContinueTarget(postDominator, containingLoop))
            {
                // A branch can bypass the chosen normal continuation by falling
                // directly into the loop latch. Once laid out before that
                // continuation, the fallthrough needs an explicit continue.
                foreach (var tail in initialThen.Concat(initialElse).Distinct().Where(candidate =>
                             !candidate.Hidden && candidate.To.Count == 1 &&
                             !CanReachWithoutLoopBackEdge(postDominator, candidate, containingLoop.Header) &&
                             !IsContinueTarget(candidate, containingLoop) &&
                             (candidate.To[0] == containingLoop.Header ||
                              IsContinueTarget(candidate.To[0], containingLoop)) &&
                             candidate.Statements.GetCondition() == null &&
                             !candidate.Statements.Any(node => node is
                                 ContinueStatement or ReturnExpression or ThrowExpression)))
                {
                    tail.Statements.Add(new ContinueStatement());
                }
            }
            var materializationPlan = IfRegionMaterializationAnalyzer.Analyze(
                thenEntry, elseEntry, postDominator, initialThen, initialElse);

            // 分支内的多块控制流可能已先折叠进入口块，重新收集时只剩一个可见
            // 块。若另一边正是后支配继续点，它仍是完整的单臂 if，不能退回旧
            // 逻辑后留下裸条件并无条件执行入口块中的聚合语句。
            if (materializationPlan.Kind ==
                IfRegionMaterializationKind.ThenSingleArm)
            {
                logic.Then.Type = LogicalBlockType.BlockList;
                logic.Then.Blocks = materializationPlan.ThenBlocks.ToList();
                logic.Else.Type = LogicalBlockType.None;
                logic.Else.Blocks = new List<Block>();
                return true;
            }

            if (materializationPlan.Kind ==
                IfRegionMaterializationKind.ElseSingleArm)
            {
                logic.Condition = logic.Condition.Invert();
                logic.Then.Type = LogicalBlockType.BlockList;
                logic.Then.Blocks = materializationPlan.ElseBlocks.ToList();
                logic.Else.Type = LogicalBlockType.None;
                logic.Else.Blocks = new List<Block>();
                return true;
            }

            // 单块分支交给原有逻辑，它对 break/continue 和 else-if 有更细处理。
            if (materializationPlan.Kind == IfRegionMaterializationKind.None)
            {
                return false;
            }

            var nestedCandidates = materializationPlan.NestedCandidates;
            var ownedBlocks = materializationPlan.ThenBlocks
                .Concat(materializationPlan.ElseBlocks)
                .Append(conditionBlock)
                .ToHashSet();

            // 先恢复“有载荷的一臂落入另一臂”的叶子判断。若先合并外围短路链，
            // `if (enabled) { if (value >= 0) value++; clear(); }` 的内层比较会被
            // 当作普通区域节点复制，递增因此退化成无条件执行。
            StructurePayloadLeafConditions(nestedCandidates);

            // 外层支配区域一旦物化便会隐藏其中全部基本块。先从最早入口恢复
            // 区域内的 &&/|| 链，再从尾部恢复普通嵌套判断，避免条件节点被当作
            // 裸比较复制，而其控制的赋值、递增随后变成无条件语句。
            StructureNestedConditionChains(nestedCandidates);
            foreach (var candidate in nestedCandidates
                         .OrderByDescending(candidate => candidate.Start))
            {
                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition == null)
                {
                    continue;
                }

                if (TryStructureIfElseWithinOwnership(
                        candidate, postDominator, ownedBlocks,
                        out var nestedLogic, out var ownershipViolation))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedLogic.Simplify().ToStatement());
                }
                else if (ownershipViolation)
                {
                    // 子候选一旦越过冻结区域边界，整个父候选都必须回滚；继续
                    // 使用部分物化结果会让公共尾部在后续扫描中永久不可见。
                    return false;
                }
            }

            // 子条件只能改变计划内块的可见性，不能在 AST 已变化后重新扫描 CFG
            // 并取得新块。由冻结的所有权集合解析最终可见布局。
            var layout = materializationPlan.ResolveVisibleLayout();
            if (layout.InvertCondition)
            {
                logic.Condition = logic.Condition.Invert();
            }
            logic.Then.Type = layout.ThenBlocks.Count == 0
                ? LogicalBlockType.None
                : LogicalBlockType.BlockList;
            logic.Then.Blocks = layout.ThenBlocks.ToList();
            logic.Else.Type = layout.ElseBlocks.Count == 0
                ? LogicalBlockType.None
                : LogicalBlockType.BlockList;
            logic.Else.Blocks = layout.ElseBlocks.ToList();
            return layout.HasContent;
        }

        /// <summary>
        /// 从后向前物化“一臂含副作用并落入另一臂”的叶子 if。它们通常位于
        /// 外围短路守卫的正文末端；若等外围区域先收集，条件会退化成裸比较，
        /// 自增、赋值等载荷则会被错误提升为无条件语句。
        /// </summary>
        private void StructurePayloadLeafConditions(IEnumerable<Block> candidates)
        {
            if (candidates == null || _context?.BlockTable == null)
            {
                return;
            }

            foreach (var candidate in candidates
                         .Distinct()
                         .OrderByDescending(block => block.Start))
            {
                var condition = candidate.Statements.GetCondition();
                if (condition == null ||
                    !_context.BlockTable.TryGetValue(
                        condition.TrueBranch, out var trueTarget) ||
                    !_context.BlockTable.TryGetValue(
                        condition.FalseBranch, out var falseTarget))
                {
                    continue;
                }

                // 公共尾部若还能从更外层条件直接到达，它属于短路表达式之后的
                // 顺序代码（如 A && B 后的 cleanup），此处不能提前拆开 A/B。
                // 只处理汇合块仍由当前叶子条件支配、确定处在外围正文内部的形状。
                bool HasSimpleMutationPayload(Block target) =>
                    HasBranchPayload(target) &&
                    target.Statements.Any(statement =>
                        statement is BinaryExpression or UnaryExpression) &&
                    target.Statements.All(statement =>
                        statement is BinaryExpression or UnaryExpression or
                            InvokeExpression or GotoExpression);
                var trueFallsIntoFalse =
                    trueTarget.To.Contains(falseTarget) &&
                    HasSimpleMutationPayload(trueTarget) &&
                    falseTarget.Dominator?[candidate.Id] == true;
                var falseFallsIntoTrue =
                    falseTarget.To.Contains(trueTarget) &&
                    HasSimpleMutationPayload(falseTarget) &&
                    trueTarget.Dominator?[candidate.Id] == true;
                var isPayloadLeaf = trueFallsIntoFalse || falseFallsIntoTrue;
                if (isPayloadLeaf && StructureIfElse(candidate, out var leafLogic))
                {
                    candidate.Statements.Replace(
                        condition, leafLogic.Simplify().ToStatement());
                    continue;
                }

                // 内层载荷的公共尾部也可能由外层条件直接绕过到达。此时尾部
                // 不受当前条件支配，但独占载荷臂仍能安全恢复为单臂 if。
                var conditionIndex = candidate.Statements.IndexOf(condition);
                var hasPrefixMutation = conditionIndex > 0 &&
                    candidate.Statements.Take(conditionIndex).Any(statement =>
                        statement is BinaryExpression or UnaryExpression);
                bool IsUniquePayloadArm(Block payload, Block continuation) =>
                    hasPrefixMutation &&
                    payload.To.Contains(continuation) &&
                    HasSimpleMutationPayload(payload) &&
                    payload.From.All(predecessor => predecessor == candidate);
                var sharedContinuation = IsUniquePayloadArm(trueTarget, falseTarget)
                    ? falseTarget
                    : IsUniquePayloadArm(falseTarget, trueTarget)
                        ? trueTarget
                        : null;
                if (sharedContinuation != null)
                {
                    var payload = sharedContinuation == falseTarget
                        ? trueTarget
                        : falseTarget;
                    var logic = new IfLogic
                    {
                        ConditionBlock = candidate,
                        Condition = payload.Start == condition.TrueBranch
                            ? condition.Condition
                            : condition.Condition.Invert(),
                        PostDominator = sharedContinuation,
                        Then =
                        {
                            Type = LogicalBlockType.BlockList,
                            Blocks = new List<Block> { payload }
                        },
                        Else = { Type = LogicalBlockType.None }
                    };
                    candidate.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 内层赋值判断已物化后，继续从内向外恢复包含它的单臂区域。显式指定
        /// CFG 的公共继续块，避免通用启发式误把内层汇合点当成外层汇合点，
        /// 从而将同一源码 if 的后半段提升到作用域之外。
        /// </summary>
        private void StructureMaterializedSingleArmRegions()
        {
            foreach (var block in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             candidate.To.Count == 2 &&
                                             candidate.Statements.GetCondition() != null &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                var condition = block.Statements.GetCondition();
                var conditionIndex = block.Statements.IndexOf(condition);
                var hasConditionPrefixMutation = conditionIndex > 0 &&
                    block.Statements.Take(conditionIndex).Any(statement =>
                        statement is BinaryExpression or UnaryExpression);
                if (!hasConditionPrefixMutation)
                {
                    continue;
                }

                var continuation = block.To.FirstOrDefault(target =>
                    block.To.Any(other => other != target &&
                        CanReach(other, target)));
                if (continuation == null)
                {
                    continue;
                }

                var body = block.To.First(target => target != continuation);
                var firstNestedIf = body.Statements.FindIndex(statement =>
                    statement is IfStatement);
                var hasPrefixMutation = firstNestedIf > 0 &&
                    body.Statements.Take(firstNestedIf).Any(statement =>
                        statement is BinaryExpression or UnaryExpression);
                var visibleBodyRegion = body.Hidden
                    ? CollectDominatedBranchRegion(
                        block, body, continuation, true)
                    : new List<Block>();
                var hasCollapsedValuePayload = body.Hidden &&
                    visibleBodyRegion.Any(candidate =>
                        candidate.Statements.Any(statement =>
                            statement is BinaryExpression or UnaryExpression));
                if (body.Statements.GetCondition() != null ||
                    (!hasPrefixMutation && !hasCollapsedValuePayload) ||
                    !AllPathsReachOrTerminateAt(body, continuation))
                {
                    continue;
                }

                if (!_structuringBlocks.Add(block.Id))
                {
                    continue;
                }

                try
                {
                    if (StructureIfElseCore(
                            block, out var logic, true, continuation))
                    {
                        block.Statements.Replace(
                            condition, logic.Simplify().ToStatement());
                    }
                }
                finally
                {
                    _structuringBlocks.Remove(block.Id);
                }
            }
        }
    }
}
