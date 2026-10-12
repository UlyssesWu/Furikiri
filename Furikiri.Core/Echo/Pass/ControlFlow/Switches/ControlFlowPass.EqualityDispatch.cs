using System;
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
        /// 优先物化已经由缓存选择值和 CFG 边共同证明的相等分派。
        /// 分析器保持只读；只有完整计划成功后才会隐藏 case 区域。
        /// </summary>
        private void StructureEqualityDispatches(IEnumerable<Block> candidates)
        {
            foreach (var block in candidates
                         .Where(candidate => !candidate.Hidden &&
                                             candidate.Statements.GetCondition() != null &&
                                             EqualityDispatchAnalyzer.HasCachedSelector(candidate))
                         .OrderBy(candidate => candidate.Start)
                         .ToList())
            {
                if (block.Hidden)
                {
                    continue;
                }

                var condition = block.Statements.GetCondition();
                if (condition != null &&
                    TryStructureEqualitySwitchChain(block, out var logic))
                {
                    block.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
                else if (condition != null)
                {
                    var loop = _context.LoopSet
                        .Where(candidate => candidate.Contains(block))
                        .OrderBy(candidate => candidate.Blocks.Count)
                        .FirstOrDefault();
                    if (loop != null &&
                        TryStructureLoopSwitchBreakDispatch(
                            block, loop, out logic))
                    {
                        block.Statements.Replace(
                            condition, logic.Simplify().ToStatement());
                    }
                }
            }
        }

        /// <summary>
        /// 恢复编译器生成的相等比较 case 链。多个 case 可先跳到同一标签块，
        /// 再共用一个源码分支；按“比较值 -&gt; 真实分支入口”分组，避免把分支
        /// 开头的嵌套 if 继续误认成短路条件。
        /// </summary>
        private bool TryStructureEqualitySwitchChain(Block root, out IfLogic logic)
        {
            logic = null;
            if (!EqualityDispatchAnalyzer.TryAnalyze(
                    _context, root, NormalizeEqualityDispatchTarget,
                    out var dispatch))
            {
                return false;
            }

            var labels = dispatch.Labels;
            var caseBlocks = dispatch.ConditionBlocks;
            var groups = dispatch.Groups;
            var defaultTarget = dispatch.DefaultTarget;
            // 此时条件链尚未隐藏，先从原始指令空洞冻结 default 的来源证据。
            // 后续区域物化只能消费该事实，不能根据已经重写的 AST 反向猜测。
            var hasCompilerDefaultEvidence =
                SwitchCompilationPatternAnalyzer.HasExplicitDefaultClause(
                    _context, dispatch) ||
                dispatch.Groups.Any(group =>
                    group.Target == dispatch.DefaultTarget);

            var postDominator = FindIfPostDominator(root);
            if (postDominator == null)
            {
                return false;
            }

            // 多个 case 的“不执行正文”路径可共同落到函数末尾的隐式 return。
            // 该块是 switch 后的正常继续点，不是源码 default；若仍按 default
            // 正文认领，case guard 会被标成贯穿，既丢 break 又可能让后一 case
            // 在前一 case 未命中内部 guard 时继续执行。
            var hasSharedImplicitVoidContinuation =
                IsSharedVoidContinuation(defaultTarget) &&
                groups.Count(group => CanReach(group.Target, defaultTarget)) >= 2;
            if (hasSharedImplicitVoidContinuation)
            {
                postDominator = defaultTarget;
                hasCompilerDefaultEvidence = false;
            }

            var containingLoop = _context.LoopSet
                .Where(loop => loop.Contains(root))
                .OrderBy(loop => loop.Blocks.Count)
                .FirstOrDefault();
            bool ReachesDefaultWithoutNextIteration(Block start)
            {
                return containingLoop == null
                    ? CanReach(start, defaultTarget)
                    : CanReachWithoutLoopBackEdge(
                        start, defaultTarget, containingLoop.Header);
            }

            // 通过循环回边在下一轮重新到达分派不属于 case fallthrough。
            // 只保留当前迭代内进入 default 的路径，否则每个 continue case
            // 都会被误标成贯穿 default。
            var hasDefaultFallthrough = groups.Any(group =>
                ReachesDefaultWithoutNextIteration(group.Target));
            var containingLoopExit = containingLoop?.Break ??
                                     (containingLoop?.LoopLogic as DoWhileLogic)?.Break;
            if (containingLoopExit != null && postDominator == containingLoopExit &&
                groups.Any(group => !containingLoop.Contains(group.Target)))
            {
                // 多个相等比较命中同一正文并随即退出循环时，公共后支配点就是
                // 循环 break。switch 风格恢复依赖自然落入后支配点，会在循环 AST
                // 中丢掉 break；交给通用条件链显式收集正文并补上退出语句。
                // 即使放弃结构化，也要先恢复 switch 控制调用的一次求值语义。
                HoistRepeatedInvocationComparisonTarget(root, caseBlocks);
                return false;
            }

            // 提前 return 会使真正的正常流公共尾部不再是图论后支配点。
            // 相等分派的 default 入口若能由至少一个 case 到达，且所有 case
            // 都只会到达它或提前结束，则它其实是分派后的公共继续块。
            var defaultSharesCaseBody = groups.Any(group =>
                group.Target == defaultTarget);
            var defaultIsNormalContinuation = !defaultSharesCaseBody &&
                defaultTarget != postDominator &&
                groups.Any(group =>
                    ReachesDefaultWithoutNextIteration(group.Target)) &&
                (groups.All(group => AllPathsReachOrTerminateAt(group.Target, defaultTarget)) ||
                 !hasCompilerDefaultEvidence && containingLoop != null &&
                 !groups.Any(group => IsContinueTarget(group.Target, containingLoop)) &&
                 groups.All(group => AllPathsReachOrTerminateAt(group.Target, defaultTarget, containingLoop)));
            if (caseBlocks.Count == 1 && defaultIsNormalContinuation &&
                !SwitchCompilationPatternAnalyzer.IsSingleCaseSwitch(
                    _context, root))
            {
                // 只有一次相等比较时，命中臂若仍汇入未命中入口，CFG 只能证明
                // “带缓存选择值的单臂 if”，不能证明源码存在 switch。尤其是
                // `x == c && predicate` 会产生相同图形。只有控制流改写前冻结的
                // 编译器收尾跳转能解除歧义；证据不足时保留条件结构，避免把
                // 后续 else-if 错收进分派区域。
                return false;
            }
            if (defaultIsNormalContinuation)
            {
                postDominator = defaultTarget;
            }
            else if (!defaultSharesCaseBody &&
                     defaultTarget != postDominator && containingLoop == null &&
                     groups[0].Target != postDominator)
            {
                // default 本身也可能有正文，此时真正的公共尾部位于它之后。
                // 若某些 case 提前 return/throw，图论后支配点会退化到函数出口；
                // 搜索所有分派臂都“到达或提前终止”的最近后续块，可恢复源码中
                // 带终止 case 的 switch，而不把 default 正文误当成公共尾部。
                var normalContinuation = _context.Blocks
                    .Where(candidate => candidate != root &&
                                        candidate != postDominator &&
                                        candidate.Start > root.Start &&
                                        CanReach(defaultTarget, candidate) &&
                                        groups.Any(group =>
                                            CanReach(group.Target, candidate)))
                    .OrderBy(candidate => candidate.Start)
                    .FirstOrDefault(candidate =>
                        AllPathsReachOrTerminateAt(defaultTarget, candidate) &&
                        groups.All(group =>
                            AllPathsReachOrTerminateAt(group.Target, candidate)));
                if (normalContinuation != null)
                {
                    postDominator = normalContinuation;
                }
            }

            // default 仍能只经过条件块回到既有 case 体时，这不是互斥的 switch，
            // 而是 `a == x || a == y || (P && Q)` 一类短路谓词。交给通用条件链
            // 一次性恢复，否则共享真分支会只挂在最后一项，前面的 case 变成空壳。
            if (defaultTarget != postDominator && groups.Any(group =>
                    group.Target != defaultTarget &&
                    CanReachThroughConditionBlocks(defaultTarget, group.Target)))
            {
                return false;
            }

            // 循环中的首个空 case 需要由普通分支恢复把公共后续处理放回
            // break 路径；在 equality 区域中直接物化会改变嵌套条件的归并顺序。
            if (groups[0].Target == postDominator && containingLoop != null)
            {
                return false;
            }

            var initialRegions = groups.ToDictionary(
                group => group.Target,
                group => group.Target == postDominator
                    ? new List<Block>()
                    : CollectDominatedBranchRegion(root, group.Target, postDominator, true));
            if (initialRegions.Any(pair => pair.Key != postDominator && pair.Value.Count == 0))
            {
                return false;
            }

            var sharedTails = containingLoop == null ||
                              postDominator != containingLoop.Header &&
                              !IsContinueTarget(postDominator, containingLoop)
                ? new List<Block>() : _context.Blocks
                .Where(candidate => !candidate.Hidden && candidate != postDominator &&
                    initialRegions.Values.Any(region => region.Contains(candidate)) &&
                    candidate.Dominator != null && candidate.Dominator[root.Id] &&
                    candidate.To.Count == 1 &&
                    (candidate.To[0] == postDominator ||
                     IsContinueTarget(postDominator, containingLoop) &&
                     IsContinueTarget(candidate.To[0], containingLoop)) &&
                    !IsContinueTarget(candidate, containingLoop) &&
                    candidate.Statements.GetCondition() == null && HasBranchPayload(candidate) &&
                    groups.Count(group => CanReachWithoutLoopBackEdge(
                        group.Target, candidate, containingLoop.Header)) > 1).ToList();
            foreach (var tail in sharedTails)
            {
                // Every outgoing edge ends this iteration. Keep that transfer
                // when a nested branch captures a copy of the shared payload.
                if (!tail.Statements.Any(node => node is ContinueStatement))
                    tail.Statements.Add(new ContinueStatement());
            }
            var sharedTailStatements = sharedTails.ToDictionary(tail => tail,
                tail => tail.Statements.Where(node => node is not GotoExpression)
                    .Select(node => node is Expression expression
                        ? (IAstNode)new ExpressionStatement(expression) : node).ToList());

            // 外层 case 正文中可以继续包含完整的相等分派。若先按普通嵌套
            // 条件物化，空 case 跳板会被合并成反向 &&，default 正文随后与
            // 另一 case 的载荷顺序输出。先在冻结区域中递归认领已有定义身份
            // 的分派，使内外两层分别取得自己的条件块和正文所有权。
            StructureEqualityDispatches(
                initialRegions.Values.SelectMany(region => region));

            // case 区域中的 `if (A) { if (B) X; else Y; }` 两层分支可能共用
            // 同一汇合点。它不是短路链，因为内层两臂都有实际载荷；必须在
            // equality 分派复制 case 正文之前先物化，避免外层保存裸比较快照。
            StructureStrictlyNestedDecisions(
                initialRegions.Values.SelectMany(region => region));

            // case 入口本身可能是“条件为假就直接 continue”的多块单臂 if。
            // 若先从区域尾部结构化，其全部后继会被隐藏，稍后入口只剩裸条件；
            // 因此这类以循环回边为明确边界的入口必须优先整体物化。
            foreach (var candidate in groups.Select(group => group.Target).Distinct()
                         .Where(candidate => candidate != postDominator))
            {
                var candidateCondition = candidate.Statements.GetCondition();
                if (candidateCondition == null)
                {
                    continue;
                }

                var candidateLoop = _context.LoopSet
                    .Where(loop => loop.Contains(candidate))
                    .OrderBy(loop => loop.Blocks.Count)
                    .FirstOrDefault();
                if (candidateLoop == null ||
                    !_context.BlockTable.TryGetValue(
                        candidateCondition.TrueBranch, out var candidateTrue) ||
                    !_context.BlockTable.TryGetValue(
                        candidateCondition.FalseBranch, out var candidateFalse))
                {
                    continue;
                }

                var trueContinues = IsContinueTarget(candidateTrue, candidateLoop);
                var falseContinues = IsContinueTarget(candidateFalse, candidateLoop);
                if (trueContinues == falseContinues)
                {
                    continue;
                }

                var continueTarget = trueContinues ? candidateTrue : candidateFalse;
                var bodyEntry = trueContinues ? candidateFalse : candidateTrue;
                var initialGuardedRegion = CollectDominatedBranchRegion(
                    candidate, bodyEntry, continueTarget, true);
                if (initialGuardedRegion.Count == 0)
                {
                    continue;
                }

                StructureNestedConditionChains(initialGuardedRegion);
                foreach (var nestedCandidate in initialGuardedRegion
                             .Where(block => block != candidate)
                             .OrderByDescending(block => block.Start))
                {
                    var nestedCondition = nestedCandidate.Statements.GetCondition();
                    if (nestedCondition != null &&
                        StructureIfElse(nestedCandidate, out var nestedLogic))
                    {
                        nestedCandidate.Statements.Replace(
                            nestedCondition, nestedLogic.Simplify().ToStatement());
                    }
                }

                var guardedRegion = CollectDominatedBranchRegion(
                    candidate, bodyEntry, continueTarget, true);
                if (guardedRegion.Count == 0)
                {
                    continue;
                }

                var guardedCaseLogic = new IfLogic
                {
                    ConditionBlock = candidate,
                    Condition = trueContinues
                        ? candidateCondition.Condition.Invert()
                        : candidateCondition.Condition,
                    PostDominator = continueTarget,
                    Then =
                    {
                        Type = LogicalBlockType.BlockList,
                        Blocks = guardedRegion
                    },
                    Else = { Type = LogicalBlockType.None }
                };
                candidate.Statements.Replace(
                    candidateCondition, guardedCaseLogic.Simplify().ToStatement());
            }

            // equality case 链的 default 入口可能本身是一条范围/短路条件链，
            // 例如 '.', '-' 两个 case 之后再判断 '0' <= ch <= '9'。
            // default 若只按原始基本块收集，条件节点会被过滤而两个分支顺序输出。
            if (defaultTarget != postDominator)
            {
                var defaultCondition = defaultTarget.Statements.GetCondition();
                // 带准备赋值的 default 入口也可能正是另一条相等 case 链的根。
                // 这类链必须先整体恢复，不能先从尾部物化，否则共享 case 体会
                // 被拆成空分支并把赋值提升为无条件执行。
                if (defaultCondition != null &&
                    TryStructureEqualitySwitchChain(defaultTarget, out var nestedDefaultEquality))
                {
                    defaultTarget.Statements.Replace(
                        defaultCondition, nestedDefaultEquality.Simplify().ToStatement());
                    defaultCondition = null;
                }
                else if (defaultCondition != null &&
                         TryStructureConditionChain(defaultTarget, defaultCondition,
                             out var defaultDecision))
                {
                    // Preserve every incoming short-circuit path before a
                    // nested guard can consume the shared success/return leaf.
                    defaultTarget.Statements.Replace(
                        defaultCondition, defaultDecision.Simplify().ToStatement());
                    defaultCondition = null;
                }

                var defaultRegion = CollectDominatedBranchRegion(
                    root, defaultTarget, postDominator, true);
                var defaultNested = defaultRegion
                    .Where(candidate => candidate != defaultTarget)
                    .ToList();

                // default 正文中的两臂载荷菱形必须先于入口条件恢复。例如外层
                // `if (face != "")` 的真臂里还有“拆分列表/直接添加”二选一；若
                // default 入口先保存整片块快照，内层两个条件会以裸比较留在快照
                // 中，随后对 CFG 的修复也无法回写。这里只处理具有独立双载荷的
                // 严格嵌套区域，不会拆开范围判断或短路条件链。
                StructureStrictlyNestedDecisions(defaultNested);

                // 先抢救 default 区域末端“有载荷的一侧直达另一侧”的叶子 if。
                // 若先从区域入口合并短路链，入口会把这些块连同裸条件一起捕获。
                foreach (var candidate in defaultNested
                             .OrderByDescending(candidate => candidate.Start))
                {
                    var leafCondition = candidate.Statements.GetCondition();
                    if (leafCondition == null ||
                        !_context.BlockTable.TryGetValue(
                            leafCondition.TrueBranch, out var leafTrue) ||
                        !_context.BlockTable.TryGetValue(
                            leafCondition.FalseBranch, out var leafFalse))
                    {
                        continue;
                    }

                    var isPayloadLeaf =
                        leafTrue.To.Contains(leafFalse) && HasBranchPayload(leafTrue) &&
                            leafTrue.Dominator?[candidate.Id] == true ||
                        leafFalse.To.Contains(leafTrue) && HasBranchPayload(leafFalse) &&
                            leafFalse.Dominator?[candidate.Id] == true;
                    if (isPayloadLeaf &&
                        StructureIfElse(candidate, out var leafLogic))
                    {
                        candidate.Statements.Replace(
                            leafCondition, leafLogic.Simplify().ToStatement());
                    }
                }

                // default 入口本身即使也是条件块，它的后继中仍可能包含更深的
                // 完整返回树，例如先判断首字符，再按完整名称选择两个 return。
                // 仅对所有路径都终止的树从叶子提前物化；普通范围判断/循环分支
                // 仍需保留给入口条件链整体恢复，否则会拆坏 <=、>= 的组合谓词。
                if (!defaultTarget.Statements.IsCondition() ||
                    AllPathsTerminateBefore(defaultTarget, postDominator))
                {
                    StructureNestedConditionChains(defaultNested);
                    foreach (var candidate in defaultNested
                                 .OrderByDescending(candidate => candidate.Start))
                    {
                        var nestedCondition = candidate.Statements.GetCondition();
                        if (!candidate.Hidden && nestedCondition != null &&
                            StructureIfElse(candidate, out var nestedLogic))
                        {
                            candidate.Statements.Replace(
                                nestedCondition, nestedLogic.Simplify().ToStatement());
                        }
                    }
                }

                if (defaultCondition != null &&
                    StructureIfElse(defaultTarget, out var defaultLogic))
                {
                    defaultTarget.Statements.Replace(
                        defaultCondition, defaultLogic.Simplify().ToStatement());
                }
            }

            // 分支入口可能又是一张完整判定 DAG。必须在“从尾到头”处理区域
            // 内条件之前整体物化；这里只识别相等比较仍会漏掉
            // `(A && B) || (C && probe())`，尾部一旦先变成 IfStatement，入口
            // 便无法再合成所有成功路径。
            foreach (var candidate in groups.Select(group => group.Target).Distinct()
                         .Where(candidate => candidate != postDominator))
            {
                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition != null &&
                    (TryStructureCaseEntryAtSharedVoidContinuation(
                         candidate, postDominator, initialRegions,
                         out var nestedDecision) ||
                     TryStructureCaseEntryTerminalGuard(
                         candidate, postDominator, out nestedDecision) ||
                     TryStructureConditionChain(
                         candidate, nestedCondition, out nestedDecision)))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedDecision.Simplify().ToStatement());
                }
            }

            // Preserve complete short-circuit chains inside each case before
            // materializing leaves; a shared payload may bypass the last test.
            StructureNestedConditionChains(initialRegions.Values.SelectMany(region => region));

            // 先从 case 区域尾部恢复内层 if。case 入口只覆盖首个条件，后续的
            // 短路判断通常位于共享后继块；若直接物化入口，这些后继会以裸比较
            // 输出，真正受控的副作用语句则被隐藏掉。
            foreach (var candidate in initialRegions.Values.SelectMany(region => region)
                         .Distinct()
                         .Where(candidate => !candidate.Hidden &&
                                             !groups.Any(group => group.Target == candidate))
                         .OrderByDescending(candidate => candidate.Start))
            {
                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition != null && StructureIfElse(candidate, out var nestedLogic))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedLogic.Simplify().ToStatement());
                }
            }

            // 最后把每个 case 的真实入口作为整体递归结构化。入口本身也可能是
            // 另一组合法的相等比较链（如事件类型分支中的按钮目标判断），不能
            // 禁用条件链恢复，否则首个比较会以裸表达式输出，真分支副作用被提升。
            foreach (var candidate in groups.Select(group => group.Target).Distinct()
                         .Where(candidate => candidate != postDominator))
            {
                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition != null &&
                    StructureIfElse(candidate, out var nestedLogic))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedLogic.Simplify().ToStatement());
                }
            }

            var regions = groups.ToDictionary(
                group => group.Target,
                group => group.Target == postDominator ||
                         group.Target == defaultTarget &&
                         defaultTarget != postDominator
                    ? new List<Block>()
                    : CollectDominatedBranchRegion(root, group.Target, postDominator, true));

            var sharedBodies = new Dictionary<Block, BlockStatement>();
            if (sharedTails.Count > 0)
            {
                foreach (var group in groups)
                {
                    var body = new BlockStatement(regions[group.Target]
                        .Except(sharedTails).ToList(), true);
                    foreach (var tail in sharedTails.Where(tail =>
                                 CanReachWithoutLoopBackEdge(group.Target, tail, containingLoop.Header)))
                    {
                        body.Statements.AddRange(sharedTailStatements[tail]);
                    }
                    sharedBodies[group.Target] = body;
                }
            }

            // A case can reach the next case through several conditional blocks.
            // The shared successor is not dominated by the first case, so inspect
            // every region exit rather than only the case entry's direct edges.
            var directFallthroughs = groups
                .SelectMany(group => regions[group.Target].Append(group.Target)
                    .SelectMany(block => block.To).Distinct()
                    .Select(target => (From: group.Target, To: target)))
                .Where(edge => edge.From != edge.To &&
                    groups.Any(group => group.Target == edge.To) &&
                    AllPathsReachOrTerminateAt(edge.From, edge.To, containingLoop) &&
                    !regions[edge.From].Contains(edge.To)).ToDictionary(edge => edge.From, edge => edge.To);
            if (directFallthroughs.Count > 0)
            {
                var snapshots = groups.ToDictionary(group => group.Target,
                    group => sharedBodies.TryGetValue(group.Target, out var body)
                        ? body : new BlockStatement(regions[group.Target], true));
                foreach (var group in groups)
                {
                    var body = new BlockStatement();
                    var visited = new HashSet<Block>();
                    var target = group.Target;
                    do
                    {
                        if (!visited.Add(target))
                            throw new InvalidOperationException("Cyclic case fallthrough.");
                        body.Statements.AddRange(snapshots[target].Statements);
                    } while (directFallthroughs.TryGetValue(target, out target));
                    sharedBodies[group.Target] = body;
                }
            }

            LogicalBlock defaultBlock = new LogicalBlock { Type = LogicalBlockType.None };
            if (defaultTarget != postDominator)
            {
                var defaultRegion = CollectDominatedBranchRegion(
                    root, defaultTarget, postDominator, true);
                if (defaultRegion.Count > 0)
                {
                    defaultBlock.Type = LogicalBlockType.BlockList;
                    defaultBlock.Blocks = defaultRegion;
                }
            }

            HoistRepeatedInvocationComparisonTarget(root, caseBlocks);

            // defaultTarget 与公共后继相同时，它只是 switch 后的顺序代码；
            // case 到达该块属于正常 break，不是向 default 贯穿。
            var ownsDefaultRegion = defaultTarget != postDominator;
            var equalityDispatchEndsAtLoopLatch = containingLoop != null &&
                IsContinueTarget(postDominator, containingLoop);

            IfLogic nested = null;
            for (var index = groups.Count - 1; index >= 0; index--)
            {
                var group = groups[index];
                var combinedCondition = group.Conditions[0];
                for (var conditionIndex = 1; conditionIndex < group.Conditions.Count; conditionIndex++)
                {
                    combinedCondition = combinedCondition.Or(group.Conditions[conditionIndex]);
                }

                var currentLogic = new IfLogic
                {
                    IsEqualityDispatch = true,
                    HasCompilerSwitchEvidence =
                        caseBlocks.Count > 1 ||
                        SwitchCompilationPatternAnalyzer.IsSingleCaseSwitch(
                            _context, root),
                    HasCompilerDefaultEvidence =
                        hasCompilerDefaultEvidence,
                    HasEqualityDispatchDefaultFallthrough =
                        ownsDefaultRegion && hasDefaultFallthrough,
                    FallsThroughToEqualityDispatchDefault =
                        ownsDefaultRegion &&
                        ReachesDefaultWithoutNextIteration(group.Target),
                    EqualityDispatchEndsAtLoopLatch =
                        equalityDispatchEndsAtLoopLatch,
                    ConditionBlock = group.Owner,
                    Condition = combinedCondition,
                    PostDominator = postDominator,
                    Then =
                    {
                        Type = regions[group.Target].Count == 0
                            ? LogicalBlockType.None
                            : LogicalBlockType.BlockList,
                        Blocks = regions[group.Target]
                    }
                };

                if (sharedBodies.TryGetValue(group.Target, out var sharedBody))
                {
                    currentLogic.Then.Type = LogicalBlockType.Statement;
                    currentLogic.Then.Statement = sharedBody;
                }

                if (nested != null)
                {
                    currentLogic.Else.Type = LogicalBlockType.Logical;
                    currentLogic.Else.Logic = nested;
                    nested.ParentIf = currentLogic;
                }
                else
                {
                    currentLogic.Else = defaultBlock;
                }

                nested = currentLogic;
            }

            foreach (var caseBlock in caseBlocks.Where(block => block != root))
            {
                caseBlock.Hidden = true;
            }
            foreach (var label in labels)
            {
                label.Hidden = true;
            }

            logic = nested;
            if (sharedBodies.Count > 0)
            {
                regions.Values.SelectMany(region => region).Concat(sharedTails).Distinct().ToList().SafeHide();
            }
            return logic != null;
        }

        /// <summary>
        /// switch 的多个 case 可能在 guard 失败时共用函数末尾的隐式 `return;`，
        /// 成功臂则各自执行正文并带值返回。若按普通 if 先物化第一个 case，公共
        /// void-return 块会被隐藏；后续 case 再收集同一块时只得到空分支，正文便
        /// 被提升成无条件代码。这里以外层分派冻结的 case 区域为所有权边界，
        /// 将共享隐式返回只视为 case 的继续边，而不让任何单个 case 消费它。
        /// </summary>
        private bool TryStructureCaseEntryAtSharedVoidContinuation(
            Block entry, Block continuation,
            IReadOnlyDictionary<Block, List<Block>> caseRegions,
            out IfLogic logic)
        {
            logic = null;
            if (entry == null || continuation == null ||
                caseRegions == null || !caseRegions.TryGetValue(entry, out var region) ||
                !entry.To.Contains(continuation) ||
                !IsSharedVoidContinuation(continuation))
            {
                return false;
            }

            var ownedBlocks = region.ToHashSet();
            ownedBlocks.Add(entry);
            return TryStructureIfElseWithinOwnership(
                entry, continuation, ownedBlocks,
                out logic, out _);
        }

        private static bool IsSharedVoidContinuation(Block continuation)
        {
            return continuation != null &&
                   continuation.From.Count >= 2 &&
                   continuation.Statements.Count > 0 &&
                   continuation.Statements.All(statement =>
                       statement is ReturnExpression or GotoExpression) &&
                   continuation.Statements.OfType<ReturnExpression>().Any() &&
                   continuation.Statements.OfType<ReturnExpression>().All(ret =>
                       ret.Return == null);
        }

        /// <summary>
        /// case 入口的一侧提前结束、另一侧直达整个 switch 的公共尾部时，
        /// 公共尾部只表示源码中的 break，不能归给最先处理的 case 当作 else。
        /// 在递归判定 DAG 之前先按外层已确定的边界物化单臂守卫，避免随后通过
        /// Hidden 状态把同一尾部从其他 case 的视图中移除。
        /// </summary>
        private static bool TryStructureCaseEntryTerminalGuard(
            Block entry, Block switchContinuation, out IfLogic logic)
        {
            logic = null;
            if (!TerminalGuardPlanner.TryAnalyze(entry, out var plan) ||
                plan.Continuation != switchContinuation)
            {
                return false;
            }

            logic = TerminalGuardMaterializer.Materialize(plan);
            return true;
        }
    }
}
