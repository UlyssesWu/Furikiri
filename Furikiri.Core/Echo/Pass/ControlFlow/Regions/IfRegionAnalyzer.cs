using System;
using System.Collections.Generic;
using System.Linq;

namespace Furikiri.Echo.Pass
{
    internal enum IfRegionExitKind
    {
        Unknown,
        FallsThrough,
        Return,
        Throw,
        LoopTransfer,
        External,
        Mixed
    }

    /// <summary>
    /// 单个条件根的只读区域边界计划。
    /// </summary>
    internal sealed class IfRegionPlan
    {
        public Block ConditionBlock { get; set; }
        public Block ThenEntry { get; set; }
        public Block ElseEntry { get; set; }
        public Block GraphPostDominator { get; set; }
        public Block NormalContinuation { get; set; }
        public bool ThenCanReachElse { get; set; }
        public bool ElseCanReachThen { get; set; }
        public bool ThenReachesElseOrTerminates { get; set; }
        public bool ElseReachesThenOrTerminates { get; set; }
        public IReadOnlyList<Block> ThenBlocks { get; set; } = Array.Empty<Block>();
        public IReadOnlyList<Block> ElseBlocks { get; set; } = Array.Empty<Block>();
        public IfRegionExitKind ThenExit { get; set; }
        public IfRegionExitKind ElseExit { get; set; }
        public bool UsesSameIterationContinuation { get; set; }
    }

    /// <summary>
    /// 根据原始 CFG 判断条件两臂和正常继续点，不生成 AST、不隐藏基本块。
    /// </summary>
    internal static class IfRegionAnalyzer
    {
        public static IfRegionPlan Analyze(
            ControlFlowGraphAnalysis graph, Block conditionBlock,
            Block thenEntry, Block elseEntry, Block graphPostDominator,
            Block loopHeader, Func<Block, bool> terminates,
            Func<Block, bool> completesIteration,
            Func<Block, IfRegionExitKind> classifyTerminal = null)
        {
            var plan = new IfRegionPlan
            {
                ConditionBlock = conditionBlock,
                ThenEntry = thenEntry,
                ElseEntry = elseEntry,
                GraphPostDominator = graphPostDominator,
                NormalContinuation = graphPostDominator
            };
            if (graph == null || thenEntry == null || elseEntry == null ||
                thenEntry == elseEntry)
            {
                return plan;
            }

            plan.ThenCanReachElse = loopHeader == null
                ? graph.CanReach(thenEntry, elseEntry)
                : graph.CanReachWithoutEntering(thenEntry, elseEntry, loopHeader);
            plan.ElseCanReachThen = loopHeader == null
                ? graph.CanReach(elseEntry, thenEntry)
                : graph.CanReachWithoutEntering(elseEntry, thenEntry, loopHeader);

            plan.ThenReachesElseOrTerminates =
                (plan.ThenCanReachElse || terminates?.Invoke(thenEntry) == true) &&
                graph.AllPathsReachOrTerminateAt(
                    thenEntry, elseEntry, terminates, completesIteration);
            plan.ElseReachesThenOrTerminates =
                (plan.ElseCanReachThen || terminates?.Invoke(elseEntry) == true) &&
                graph.AllPathsReachOrTerminateAt(
                    elseEntry, thenEntry, terminates, completesIteration);

            var sameIterationContinuation = loopHeader == null
                ? null
                : FindSameIterationContinuation(
                    graph, thenEntry, elseEntry, loopHeader,
                    terminates, completesIteration);
            if (sameIterationContinuation != null &&
                sameIterationContinuation != graphPostDominator)
            {
                plan.NormalContinuation = sameIterationContinuation;
                plan.UsesSameIterationContinuation = true;
            }
            else if (plan.ThenCanReachElse != plan.ElseCanReachThen)
            {
                plan.NormalContinuation = plan.ThenCanReachElse
                    ? elseEntry
                    : thenEntry;
            }
            else if (plan.ThenReachesElseOrTerminates !=
                     plan.ElseReachesThenOrTerminates)
            {
                plan.NormalContinuation = plan.ThenReachesElseOrTerminates
                    ? elseEntry
                    : thenEntry;
            }

            // 先分别收集两臂在公共继续点之前可达的块，再移除双方共享的节点。
            // 共享节点属于父级 Sequence，不能复制进任一分支；这也是后续逐步替换
            // 依赖地址范围的区域收集器时需要保持的所有权不变量。
            bool StopsCurrentPath(Block candidate) =>
                completesIteration?.Invoke(candidate) == true ||
                (classifyTerminal?.Invoke(candidate) ?? IfRegionExitKind.Unknown) !=
                IfRegionExitKind.Unknown;
            var thenBlocks = graph.CollectReachableBefore(
                thenEntry, plan.NormalContinuation, loopHeader,
                StopsCurrentPath).ToHashSet();
            var elseBlocks = graph.CollectReachableBefore(
                elseEntry, plan.NormalContinuation, loopHeader,
                StopsCurrentPath).ToHashSet();
            thenBlocks.Remove(conditionBlock);
            elseBlocks.Remove(conditionBlock);
            var shared = thenBlocks.Intersect(elseBlocks).ToArray();
            thenBlocks.ExceptWith(shared);
            elseBlocks.ExceptWith(shared);
            plan.ThenBlocks = thenBlocks.OrderBy(block => block.Start).ToArray();
            plan.ElseBlocks = elseBlocks.OrderBy(block => block.Start).ToArray();
            plan.ThenExit = ClassifyExit(
                graph, thenEntry, plan.NormalContinuation, thenBlocks,
                completesIteration, classifyTerminal);
            plan.ElseExit = ClassifyExit(
                graph, elseEntry, plan.NormalContinuation, elseBlocks,
                completesIteration, classifyTerminal);

            return plan;
        }

        /// <summary>
        /// 查找不穿过循环头即可由两臂到达的第一个公共尾部。
        /// </summary>
        /// <remarks>
        /// 某些路径允许提前 return/throw 或结束本轮；它们不妨碍其余正常路径
        /// 共享顺序尾部。结果只是一项分析事实，是否采用仍由带所有权校验的
        /// 物化阶段决定。
        /// </remarks>
        private static Block FindSameIterationContinuation(
            ControlFlowGraphAnalysis graph,
            Block thenEntry,
            Block elseEntry,
            Block loopHeader,
            Func<Block, bool> terminates,
            Func<Block, bool> completesIteration)
        {
            bool StopsPath(Block block) =>
                terminates?.Invoke(block) == true ||
                completesIteration?.Invoke(block) == true;

            var thenReachable = graph.CollectReachableBefore(
                thenEntry, null, loopHeader, StopsPath).ToHashSet();
            var elseReachable = graph.CollectReachableBefore(
                elseEntry, null, loopHeader, StopsPath).ToHashSet();
            var candidates = thenReachable.Intersect(elseReachable)
                .Where(candidate => candidate != loopHeader &&
                                    !StopsPath(candidate) &&
                                    graph.AllPathsReachOrTerminateAt(
                                        thenEntry, candidate,
                                        terminates, completesIteration) &&
                                    graph.AllPathsReachOrTerminateAt(
                                        elseEntry, candidate,
                                        terminates, completesIteration))
                .ToArray();

            return candidates
                .Where(candidate => !candidates.Any(other =>
                    other != candidate &&
                    graph.CanReachWithoutEntering(
                        other, candidate, loopHeader)))
                .OrderBy(candidate => candidate.Start)
                .FirstOrDefault();
        }

        private static IfRegionExitKind ClassifyExit(
            ControlFlowGraphAnalysis graph, Block entry, Block continuation,
            ISet<Block> ownedBlocks, Func<Block, bool> completesIteration,
            Func<Block, IfRegionExitKind> classifyTerminal)
        {
            if (entry == continuation)
            {
                return IfRegionExitKind.FallsThrough;
            }

            var exits = new HashSet<IfRegionExitKind>();
            foreach (var block in ownedBlocks)
            {
                var terminal = classifyTerminal?.Invoke(block) ??
                               IfRegionExitKind.Unknown;
                if (terminal != IfRegionExitKind.Unknown)
                {
                    exits.Add(terminal);
                    // return/throw 到统一 ExitBlock 的 CFG 边只是分析用合流边，不是
                    // 源码的正常落空路径；终止块不再继续统计其后继。
                    continue;
                }
                if (completesIteration?.Invoke(block) == true)
                {
                    exits.Add(IfRegionExitKind.LoopTransfer);
                    continue;
                }

                var successors = graph.GetSuccessors(block);
                if (successors.Count == 0 && terminal == IfRegionExitKind.Unknown)
                {
                    exits.Add(IfRegionExitKind.External);
                }
                foreach (var next in successors)
                {
                    if (next == continuation)
                    {
                        exits.Add(IfRegionExitKind.FallsThrough);
                    }
                    else if (!ownedBlocks.Contains(next))
                    {
                        exits.Add(IfRegionExitKind.External);
                    }
                }
            }

            return exits.Count switch
            {
                0 => IfRegionExitKind.Unknown,
                1 => exits.First(),
                _ => IfRegionExitKind.Mixed
            };
        }
    }
}
