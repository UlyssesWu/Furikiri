using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Statements;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 控制流结构化的阶段调度入口。各职责实现位于 ControlFlow 子目录的 partial 文件中；
    /// 所有阶段共用本实例的上下文、CFG 快照和递归结构化状态，执行顺序由 Process 统一维护。
    /// </summary>
    partial class ControlFlowPass : IPass
    {
        private DecompileContext _context;
        private ControlFlowGraphAnalysis _graph;
        private readonly HashSet<int> _structuringBlocks = new HashSet<int>();

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            _context = context;
            _graph = new ControlFlowGraphAnalysis(context);
            _context.LoopSetSort();

            // 结构化会原地替换条件节点。所有依赖原始块边界和缓存定义身份的
            // 单 case 来源证据必须先冻结，避免内层分派改写后污染父候选。
            SwitchCompilationPatternAnalyzer.CaptureEvidence(context);

            NormalizeNoOpConditions();
            HideRedundantDecisionShells();
            InlineAssignmentsIntoConditionalPhiGuards();
            HideCollapsedPhiConditions();
            InlineLocalAssignmentsIntoComparisons();
            HoistLinearCachedInvocationEqualityChains();
            IntervalAnalysisDoWhilePass();

            // 先处理循环内部条件。若外围 if/try 先运行，会把尚未物化的循环基本块
            // 当成自己的普通分支并隐藏，最终造成循环体被提到循环外。
            // A loop inside an outer loop's break arm is outside that outer
            // natural-loop set. Process later headers first so the exit arm
            // sees the complete nested loop instead of consuming its raw body.
            foreach (var loop in context.LoopSet.OrderByDescending(loop => loop.Header.Start))
            {
                // 父自然循环的 Blocks 会包含子循环全部基本块。父循环若提前在这些块上
                // 恢复 if，会把子循环体当成父循环的普通分支并搬到子循环语句之外。
                // 子循环稍后会先物化为单个语句，因此这里仅处理父循环自己的块。
                var childBlocks = loop.Children
                    .SelectMany(child => child.Blocks)
                    .ToHashSet();

                // try 必须早于包住它的循环内 if 结构化；否则外围 if 会先复制原始
                // CatchExpression、正文和异常寄存器赋值，之后再写回 TryStatement 已
                // 无法修改这个 AST 快照。子循环已在前一轮完整物化。
                BuildTry(entry => loop.Contains(entry) && !childBlocks.Contains(entry));

                if (loop.LoopLogic is DoWhileLogic dw)
                {
                    StructureLoopBodyIfElse(dw.Body.Where(block => !childBlocks.Contains(block)).ToList());
                }
                else if (loop.LoopLogic is ForLogic fl)
                {
                    StructureLoopBodyIfElse(fl.Body.Where(block => !childBlocks.Contains(block)).ToList());
                }

                // 子循环在继续处理父循环前立即物化。仅仅“先分析、稍后统一物化”仍会
                // 让父循环的外围 if 沿 CFG 进入子循环，把其语句吸收到自己的分支中。
                loop.MaterializedStatement = loop.LoopLogic.ToStatement();
                loop.Header.Statements = new List<IAstNode> { loop.MaterializedStatement };
                loop.Header.Hidden = false;
            }

            // 循环外的 try 可能包住已经物化的完整循环，留到所有循环完成后收集。
            BuildTry(entry => !_context.LoopSet.Any(loop => loop.Contains(entry)));

            // Recover complete return decisions before individual comparisons can
            // claim a shared success arm and strand the remaining paths.
            foreach (var root in _context.Blocks.OrderBy(block => block.Start))
            {
                if (root.Hidden || !root.Statements.IsCondition() ||
                    _context.LoopSet.Any(loop => loop.Contains(root))) continue;
                var condition = root.Statements.GetCondition();
                if (DecisionDagAnalyzer.TryAnalyzeReturnGuard(_context, _graph, root, out var plan) &&
                    plan.Decisions.Count > 1 &&
                    plan.Decisions.Any(block => block != root && block.From.Count > 1) &&
                    TerminalConditionChainPlanner.IsSimpleTerminalReturnBlock(plan.BodyTarget))
                {
                    var returnLogic = DecisionDagMaterializer.Materialize(plan);
                    root.Statements.Replace(condition, returnLogic.Simplify().ToStatement());
                }
            }

            // 相等分派是一整片区域，必须早于叶子守卫和普通 if 物化。后者若先
            // 从外围条件收集正文，会把尚未恢复的 case 链连同路径谓词一起吞入，
            // 造成选择表达式被重复写出，甚至丢失共用 case 正文。
            StructureEqualityDispatches(_context.Blocks.Where(block =>
                !_context.LoopSet.Any(loop => loop.Contains(block))));

            // 最内层的简单赋值 if 不依赖外围区域，优先写回可避免随后构造的
            // IfStatement 保存尚未恢复的条件快照。
            StructurePayloadLeafConditions(_context.Blocks.Where(block =>
                !_context.LoopSet.Any(loop => loop.Contains(block))));
            StructureTerminalGuards(TerminalGuardStage.Throw);
            StructureTerminalGuards(TerminalGuardStage.SharedInvocation);
            StructureNestedPayloadReturnGuards();
            StructureMaterializedSingleArmRegions();
            StructureTerminalGuards(TerminalGuardStage.UpdatedValue);
            StructureTerminalGuards(TerminalGuardStage.BeforeCondition);
            StructureTerminalReturnLeaves(_context.Blocks.Where(block =>
                !_context.LoopSet.Any(loop => loop.Contains(block))));

            // 两个条件可能共享同一个回退载荷。先按完整双叶 DAG 物化，避免
            // 后续单臂区域把回退叶误认成公共继续点而漏掉第一条失败边。
            StructureTwoPayloadDecisionDags();

            // 多入口的内层分支会被外围副作用判定 DAG 一次性收进正文，必须先
            // 恢复；否则其中的条件只剩裸表达式，分支载荷被提升为顺序代码。
            // 但根谓词本身含调用的共享 DAG 必须先按父区域整体恢复；若从多前驱
            // 内层节点反向物化，会截断父条件并丢失后续短路调用。这里只接受直接
            // 后支配出口和正常汇合载荷的严格形态，不回退到普通 if。
            StructureSideEffectDecisionDags(pureDecisionDagOnly: true);
            StructureStrictlyNestedDecisions(_context.Blocks.Where(block => block.From.Count > 1));
            StructureTerminalConditionChains(TerminalConditionChainStage.EntryPayload);
            StructurePayloadPrefixedConditionChains();
            StructureSideEffectDecisionDags();

            // 外围条件链可能一次隐藏整片正文。先物化正文末端的单臂叶子 if，
            // 避免其比较和副作用在区域收集时被拆成两条无条件表达式。
            StructurePayloadLeafConditions(_context.Blocks.Where(block =>
                !_context.LoopSet.Any(loop => loop.Contains(block))));

            // 外围 if/else 会一次隐藏整片支配区域；先抢救其中带独立清理尾部的
            // 嵌套守卫。匹配条件严格要求外层有一条边直达自身汇合点。
            StructureTerminalConditionChains(TerminalConditionChainStage.PureCondition);
            StructurePayloadPrefixedConditionChains();
            StructureMultiPredecessorSharedBodyConditionChains();
            StructureSharedTrailingConditionChains();
            StructureTerminalTwoConditionGuards();
            StructureSequentialNestedGuards();
            StructureMergedBodyGateConditions();
            StructureJoinedLeafConditions();
            StructurePayloadRootSharedBodyConditionChains();
            StructureNestedSharedBodyConditionChains();
            StructureStrictlyNestedDecisions(_context.Blocks);

            // 条件链统一从最早入口处理。只筛多路链会让同一短路表达式的后半段
            // 在本轮先被物化，下一轮再看根节点时共享出口已隐藏，最终产生空分支。
            // 外围 gate 不能直接拉平的情形会返回 false，随后仍可处理其内层链。
            foreach (var b in _context.Blocks)
            {
                if (b.Hidden || context.LoopSet.Any(loop => loop.Header == b))
                {
                    continue;
                }

                var condition = b.Statements.GetCondition();
                if (condition != null &&
                    TryStructureConditionChain(b, condition, out var multiWayLogic))
                {
                    b.Statements.Replace(condition, multiWayLogic.Simplify().ToStatement());
                }
            }

            // 短路链必须从最早的根条件开始恢复。若先递归物化外层分支，后面的
            // 中间条件块会各自变成 if，根节点便无法再识别完整的 && / || 链。
            foreach (var b in _context.Blocks)
            {
                if (b.Hidden || context.LoopSet.Any(loop => loop.Header == b))
                {
                    continue;
                }

                var condition = b.Statements.GetCondition();
                if (condition != null && TryStructureConditionChain(b, condition, out var chainLogic))
                {
                    b.Statements.Replace(condition, chainLogic.Simplify().ToStatement());
                }
            }

            // 普通嵌套 if 从内向外物化。若先处理外层，分支入口处尚未恢复的
            // ConditionExpression 会和其真分支一起被收进普通语句区域，结果是
            // 条件退化为裸比较、原本受控的副作用被错误提升为无条件执行。
            // 短路条件链已在上面的专用阶段从根节点处理，因此这里倒序不会拆散 &&/||。
            foreach (var b in _context.Blocks.OrderByDescending(block => block.Start))
            {
                if (b.Hidden || context.LoopSet.Any(loop => loop.Header == b))
                {
                    continue;
                }

                var originalCondition = b.Statements.GetCondition();
                if (StructureIfElse(b, out var logic))
                {
                    b.Statements.Replace(originalCondition, logic.Simplify().ToStatement());
                }
            }

            return statement;
        }
    }
}
