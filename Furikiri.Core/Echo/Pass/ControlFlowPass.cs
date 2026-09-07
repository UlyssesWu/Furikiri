using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Logical;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    class ControlFlowPass : IPass
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
            HoistLinearCachedInvocationEqualityChains();
            IntervalAnalysisDoWhilePass();

            // 先处理循环内部条件。若外围 if/try 先运行，会把尚未物化的循环基本块
            // 当成自己的普通分支并隐藏，最终造成循环体被提到循环外。
            foreach (var loop in context.LoopSet.OrderBy(loop => loop.Blocks.Count))
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

        /// <summary>
        /// 早退守卫可能由三段以上短路条件组成，例如
        /// `!focusable || (!numLinks &amp;&amp; !hasClick())`。若先从尾部物化，外层
        /// 条件会把正常继续区域误当成自己的 then，并隐藏后面的完整分派。先从
        /// 最早入口恢复所有“只经条件/跳板可达纯 return”的链；单条件会由通用
        /// if 恢复处理，因此这里不会抢走普通分支。
        /// </summary>
        private void StructureTerminalConditionChains(TerminalConditionChainStage stage)
        {
            foreach (var candidate in TerminalConditionChainPlanner.Create(
                         _context, stage))
            {
                var root = candidate.Root;
                if (root.Hidden)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition == null)
                {
                    continue;
                }

                IfLogic logic = null;
                var structured = stage switch
                {
                    TerminalConditionChainStage.PureCondition =>
                        TryStructureConditionChain(root, condition, out logic),
                    TerminalConditionChainStage.EntryPayload =>
                        TryStructurePureDecisionDagToReturnGuard(root, out logic) ||
                        TryStructureConditionChain(root, condition, out logic) ||
                        (candidate.HasPrefixSideEffect &&
                         TryStructurePureDecisionDagToPayload(root, out logic)),
                    _ => false
                };
                if (structured)
                {
                    root.Statements.Replace(condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 将入口处只由条件块组成、最终选择“直接 return”或“进入函数正文”的
        /// 判定 DAG 恢复成提前返回守卫。正文入口可能自身带赋值和后续条件，
        /// 因而不能继续沿整张函数 CFG 搜索终点。
        /// </summary>
        private bool TryStructurePureDecisionDagToReturnGuard(
            Block root, out IfLogic logic)
        {
            logic = null;
            if (!DecisionDagAnalyzer.TryAnalyzeReturnGuard(
                    _context, _graph, root, out var plan))
            {
                return false;
            }

            logic = DecisionDagMaterializer.Materialize(plan);
            return true;
        }


        /// <summary>
        /// 带前置调用的条件入口后面可能是一张共享节点的纯布尔 DAG，最终只在
        /// “执行一个载荷块”和“直接结束”之间选择。逐层 if 恢复会在共享节点处
        /// 错误嵌套；直接计算到载荷块的路径谓词可完整保留 OR/AND 关系。
        /// </summary>
        private bool TryStructurePureDecisionDagToPayload(Block root, out IfLogic logic)
        {
            logic = null;
            if (!DecisionDagAnalyzer.TryAnalyzePayload(
                    _context, _graph, root, out var plan))
            {
                return false;
            }

            logic = DecisionDagMaterializer.Materialize(plan);
            return true;
        }

        private void StructureTwoPayloadDecisionDags()
        {
            foreach (var root in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         block.Statements.GetCondition() != null &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)))
                         .OrderBy(block => block.Start)
                         .ToList())
            {
                var condition = root.Statements.GetCondition();
                if (condition == null ||
                    !DecisionDagAnalyzer.TryAnalyzeTwoPayloadBranches(
                        _context, _graph, root, out var plan))
                {
                    continue;
                }

                var logic = DecisionDagMaterializer.Materialize(plan);
                root.Statements.Replace(
                    condition, logic.Simplify().ToStatement());
            }
        }

        private void StructureSideEffectDecisionDags(
            bool pureDecisionDagOnly = false)
        {
            foreach (var root in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)) &&
                                         !block.Statements.IsCondition() &&
                                         block.Statements.GetCondition() != null &&
                                         (!pureDecisionDagOnly ||
                                          ExpressionEffectAnalysis.HasObservableEffect(
                                              block.Statements.GetCondition().Condition)) &&
                                             block.Statements
                                                 .Take(block.Statements.Count - 1)
                                                 .OfType<Expression>()
                                             .Any(ExpressionEffectAnalysis
                                                 .HasObservableEffectBeyondLocalInitialization))
                         // 外层区域会复制分支入口的当前语句，因此先写回内层。
                         .OrderByDescending(block => block.Start)
                .ToList())
            {
                var condition = root.Statements.GetCondition();
                // 内层判断已经物化后，入口的两条边就是普通正文/继续点。
                // 此时使用完整支配区域收集，才能把正文后半段也保留在外层 if；
                // 仍含条件 DAG 时再按路径谓词恢复。
                var hasImmediateDecisionTarget = root.To.Any(target =>
                    target.Statements.GetCondition() != null);
                if ((!pureDecisionDagOnly && !hasImmediateDecisionTarget &&
                     StructureIfElse(root, out var logic)) ||
                    TryStructurePureDecisionDagToPayload(root, out logic))
                {
                    root.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 分支入口可能先执行赋值，再以两段以上条件选择两个完整正文。例如先清除
        /// 点击锁，随后用 `locked || link == -1` 选择处理路径。若等到叶子条件
        /// 由内向外物化，入口只会保留第一段判断，赋值后的整个 else 结构也会被
        /// 拆错。这里只提前处理“一侧继续经过纯条件、另一侧已是同级出口”的形态。
        /// </summary>
        private void StructurePayloadPrefixedConditionChains(
            bool pureDecisionDagOnly = false)
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             !candidate.Statements.IsCondition() &&
                                             candidate.Statements.GetCondition() != null &&
                                             (candidate.From.Count > 1 ||
                                             candidate.Statements
                                                  .Take(candidate.Statements.Count - 1)
                                                  .OfType<Expression>()
                                                  .Any(ExpressionEffectAnalysis
                                                      .HasObservableEffectBeyondLocalInitialization)) &&
                                             candidate.To.Count == 2 &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderBy(candidate => candidate.Start)
                         .ToList())
            {
                var continuation = root.To.FirstOrDefault(target =>
                    root.To.Any(other => other != target &&
                        other.Statements.IsCondition() &&
                        CanReachThroughConditionBlocks(other, target)));
                if (continuation == null)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    // 共享条件节点的 DAG 必须优先按到载荷的完整路径谓词恢复。
                    // 普通链识别只沿某一后继展开，会把
                    // `!call0() && (A || !call1())` 截断成 `!call0()`，并丢掉
                    // 第二次调用。DAG 候选失败后再退回线性条件链。
                    (TryStructurePureDecisionDagToPayload(root, out var logic) ||
                     (!pureDecisionDagOnly &&
                      TryStructureConditionChain(root, condition, out logic))))
                {
                    root.Statements.Replace(condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 多路分派后的独立短路判断通常有多个前驱，例如按键分派结束后统一执行
        /// `if (escape || enter || space) close()`。若先恢复外围分派，这个公共尾部
        /// 会被当成最后一个 else-if 出口，既可能丢掉部分 case，也会错误继承
        /// 外围 gate。先把非循环中的纯条件公共尾部物化，外围区域便会在它之前
        /// 正确汇合，并把尾部保留为后续的独立语句。
        /// </summary>
        private void StructureSharedTrailingConditionChains()
        {
            foreach (var root in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         block.From.Count > 1 &&
                                         block.Statements.IsCondition() &&
                                         block.To.Count == 2 &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)))
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                var condition = root.Statements.GetCondition();
                if (condition != null && IsPureConditionRegion(root) &&
                    TryStructureConditionChain(root, condition, out var logic))
                {
                    root.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        private bool IsPureConditionRegion(Block root)
        {
            var pending = new Stack<Block>();
            var visited = new HashSet<Block>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                var condition = current.Statements.GetCondition();
                if (condition == null)
                {
                    continue;
                }

                // 调用、赋值和自增等求值顺序本身就是语义的一部分。此类短路链
                // 必须留给常规恢复，不能仅因入口是公共尾部而提前物化。
                if (!current.Statements.IsCondition() ||
                    ExpressionEffectAnalysis.HasObservableEffect(condition.Condition))
                {
                    return false;
                }

                foreach (var target in current.To.Where(
                             target => target.Statements.GetCondition() != null))
                {
                    pending.Push(target);
                }
            }

            return true;
        }

        /// <summary>
        /// 外围 switch/default 或长条件链会一次隐藏整个支配区域。
        /// 对 `if (A &amp;&amp; B) return value;` 这种两段条件共享失败出口、
        /// 命中后直接终止的标准形状，必须在外围区域收集前先物化。
        /// 否则中间条件会成为空 if，真实 return 块虽被标记隐藏，
        /// 却没有任何 AST 节点继续引用它。
        /// </summary>
        private void StructureTerminalTwoConditionGuards()
        {
            foreach (var root in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         block.Statements.IsCondition() &&
                                         block.To.Count == 2 &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)))
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    TryStructureTwoConditionTerminalGuard(root, out var logic))
                {
                    root.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        private bool TryStructureTwoConditionTerminalGuard(
            Block root, out IfLogic logic)
        {
            logic = null;
            var rootCondition = root?.Statements?.GetCondition();
            if (rootCondition == null || !root.Statements.IsCondition() ||
                root.To.Count != 2 || _context?.BlockTable == null)
            {
                return false;
            }

            foreach (var second in root.To.Where(block =>
                         !block.Hidden && block.Statements.IsCondition()).ToList())
            {
                var continuation = root.To.First(block => block != second);
                var secondCondition = second.Statements.GetCondition();
                if (secondCondition == null || second.To.Count != 2 ||
                    !second.To.Contains(continuation))
                {
                    continue;
                }

                var body = second.To.First(block => block != continuation);
                // 多段 `A && B || C && D` 会让多个条件共同进入同一个 return
                // 主体；那属于一条完整短路链，不能只提前物化其中两段。
                if (body.From.Count != 1 || body.From[0] != second ||
                    !body.Statements.Any(statement =>
                        statement is ReturnExpression or ThrowExpression))
                {
                    continue;
                }

                var entersSecond = rootCondition.TrueBranch == second.Start
                    ? rootCondition.Condition
                    : rootCondition.Condition.Invert();
                var entersBody = secondCondition.TrueBranch == body.Start
                    ? secondCondition.Condition
                    : secondCondition.Condition.Invert();
                logic = new IfLogic
                {
                    ConditionBlock = root,
                    Condition = entersSecond.And(entersBody),
                    PostDominator = continuation,
                    Then =
                    {
                        Type = LogicalBlockType.BlockList,
                        Blocks = new List<Block> { body }
                    },
                    Else = { Type = LogicalBlockType.None }
                };
                second.Hidden = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 恢复 `if (A) { if (B) sideEffect(); if (C) returnValue(); } return fallback;`
        /// 这类顺序守卫。B 的载荷臂回到 C，而 C 的另一臂与 A 的假分支
        /// 共用 fallback。普通短路链恢复会把 A/B 合并为 `A &amp;&amp; B`，随后
        /// 吞掉 C 的调用条件。因此必须按 C、B、A 由内向外物化，并显式
        /// 保留两层的公共继续块。
        /// </summary>
        private void StructureSequentialNestedGuards()
        {
            foreach (var outer in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         block.Statements.IsCondition() &&
                                         block.To.Count == 2 &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)))
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                var outerCondition = outer.Statements.GetCondition();
                if (outerCondition == null)
                {
                    continue;
                }

                foreach (var first in outer.To.Where(HasPayloadArmJoiningSibling).ToList())
                {
                    var fallback = outer.To.First(target => target != first);
                    var firstCondition = first.Statements.GetCondition();
                    var firstTrue = _context.BlockTable[firstCondition.TrueBranch];
                    var firstFalse = _context.BlockTable[firstCondition.FalseBranch];
                    var next = firstTrue.To.Contains(firstFalse) && HasBranchPayload(firstTrue)
                        ? firstFalse
                        : firstFalse.To.Contains(firstTrue) && HasBranchPayload(firstFalse)
                            ? firstTrue
                            : null;
                    var nextCondition = next?.Statements?.GetCondition();
                    if (nextCondition == null || next.To.Count != 2 ||
                        !next.To.Contains(fallback))
                    {
                        continue;
                    }

                    // C 的命中体必须是独立区域；否则可能只是普通
                    // `A &amp;&amp; B &amp;&amp; C` 短路链，不能套用本形状。
                    var guardedBody = next.To.First(target => target != fallback);
                    if (!HasBranchPayload(guardedBody) ||
                        guardedBody.Dominator == null ||
                        !guardedBody.Dominator[next.Id])
                    {
                        continue;
                    }

                    if (!StructureIfElseCore(
                            next, out var nextLogic, false, fallback))
                    {
                        continue;
                    }
                    next.Statements.Replace(
                        nextCondition, nextLogic.Simplify().ToStatement());

                    firstCondition = first.Statements.GetCondition();
                    if (firstCondition == null ||
                        !StructureIfElseCore(first, out var firstLogic, false, next))
                    {
                        continue;
                    }
                    first.Statements.Replace(
                        firstCondition, firstLogic.Simplify().ToStatement());

                    outerCondition = outer.Statements.GetCondition();
                    if (outerCondition != null &&
                        StructureIfElseCore(
                            outer, out var outerLogic, false, fallback))
                    {
                        outer.Statements.Replace(
                            outerCondition, outerLogic.Simplify().ToStatement());
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// 连续的单行成员覆盖通常共享前一个 if 的“赋值/跳过”汇合入口，因此后续
        /// 条件块会有多个前驱。外围条件若先物化，这些叶子判断会退化为裸比较，
        /// 赋值则变成无条件执行。多前驱、单臂且主体有普通载荷的约束可将它们与
        /// 只有一个条件前驱的 &&/|| 尾块区分开。
        /// </summary>
        private void StructureJoinedLeafConditions()
        {
            foreach (var block in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             (candidate.From.Count > 1 ||
                                              IsExplicitReturnJoinedLeaf(candidate)) &&
                                             candidate.Statements.GetCondition() != null &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                // 前一个候选的区域物化可能已经隐藏或替换当前块；候选列表是阶段
                // 开始时的快照，使用前必须重新确认条件节点仍然存在。
                if (block.Hidden || block.Statements.GetCondition() == null)
                {
                    continue;
                }

                var explicitReturnLeaf = IsExplicitReturnJoinedLeaf(block);
                var continuation = block.To.FirstOrDefault(candidate =>
                    block.To.Any(other => other != candidate && CanReach(other, candidate)));
                if (continuation == null)
                {
                    // 纯条件块可能是 `A && B` / `A || B` 的共享尾谓词；这里提前
                    // 物化会让外层条件失去 else。完整双臂抢救只用于三元式合流后
                    // 同块还带真实赋值等前置载荷的形态。
                    if (block.Statements.IsCondition())
                    {
                        continue;
                    }

                    // 前一条三元表达式会让后续条件拥有两个前驱；若该条件自身又是
                    // 完整 if/else，两臂不会直达彼此，而是分别给同一成员赋值后汇合。
                    // 外围区域若先物化，这个条件就会退化成裸比较，两次赋值均变成
                    // 无条件执行。仅在两臂都有载荷且明确汇合到同一后支配点时抢救。
                    var postDominator = FindIfPostDominator(block);
                    var joinedCondition = block.Statements.GetCondition();
                    var trueTarget = block.To.FirstOrDefault(target =>
                        target.Start == joinedCondition.TrueBranch) ?? block.To[0];
                    var falseTarget = block.To.FirstOrDefault(target =>
                        target.Start == joinedCondition.FalseBranch) ?? block.To[1];
                    trueTarget = NormalizeConsumedConditionalPhiTarget(trueTarget);
                    falseTarget = NormalizeConsumedConditionalPhiTarget(falseTarget);

                    if (postDominator == null || trueTarget == falseTarget ||
                        trueTarget == postDominator || falseTarget == postDominator ||
                        !HasBranchPayload(trueTarget) || !HasBranchPayload(falseTarget) ||
                        trueTarget.Statements.Any(statement =>
                            statement is ReturnExpression or ThrowExpression) ||
                        falseTarget.Statements.Any(statement =>
                            statement is ReturnExpression or ThrowExpression) ||
                        !CanReach(trueTarget, postDominator) ||
                        !CanReach(falseTarget, postDominator))
                    {
                        continue;
                    }

                    if (StructureIfElse(block, out var branchLogic))
                    {
                        block.Statements.Replace(
                            joinedCondition, branchLogic.Simplify().ToStatement());
                    }
                    continue;
                }

                var body = block.To.First(candidate => candidate != continuation);
                if (!HasBranchPayload(body) ||
                    body.Statements.Any(statement => statement is ReturnExpression or ThrowExpression))
                {
                    continue;
                }

                var condition = block.Statements.GetCondition();
                if (condition != null && StructureIfElse(block, out var logic))
                {
                    block.Statements.Replace(condition, logic.Simplify().ToStatement());

                    // 此类叶节点的父块在比较前还会更新恢复状态。若只处理叶节点，
                    // 更外层区域仍会把父条件当普通表达式收走，使公共回调变成无条件
                    // 提前返回；因此在 CFG 边仍完整时紧接着物化父条件。
                    if (explicitReturnLeaf && block.From.Count == 1)
                    {
                        var parent = block.From[0];
                        var parentCondition = parent.Hidden
                            ? null
                            : parent.Statements.GetCondition();
                        var sharedContinuation = parent.To.FirstOrDefault(
                            target => target != block);
                        if (parentCondition != null &&
                            StructureIfElseCore(
                                parent, out var parentLogic, false,
                                sharedContinuation))
                        {
                            parent.Statements.Replace(
                                parentCondition, parentLogic.Simplify().ToStatement());
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 比较仅控制一次副作用、两路随后汇合到同一个带返回值的 return 时，应先
        /// 恢复内层单臂 if。要求显式返回值可避免把普通的 `A && B` 清理守卫拆开；
        /// 后者通常汇合到公共后续语句或隐式的 `return void`。
        /// </summary>
        private bool IsExplicitReturnJoinedLeaf(Block block)
        {
            var condition = block?.Statements?.GetCondition();
            if (condition == null || !block.Statements.IsCondition() ||
                block.From.Count != 1 || block.From[0].Statements.IsCondition() ||
                block.From[0].Statements.GetCondition() == null ||
                _context.BlockTable == null ||
                !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
            {
                return false;
            }

            static bool ReturnsValue(Block candidate) =>
                candidate?.Statements?.OfType<ReturnExpression>()
                    .Any(returnExpression => returnExpression.Return != null) == true;

            static bool IsSideEffectArm(Block candidate) =>
                candidate != null && HasBranchPayload(candidate) &&
                !candidate.Statements.Any(statement =>
                    statement is ReturnExpression or ThrowExpression);

            // 带值 return 只能由当前叶条件和其直接副作用臂拥有。若还有外层
            // 条件前驱，它是父级 Sequence 的公共出口，提前物化会隐藏其他
            // 路径仍需执行的最终 return。
            bool OwnsReturn(Block returnTarget, Block sideEffectArm) =>
                returnTarget?.From.All(predecessor =>
                    predecessor == block || predecessor == sideEffectArm) == true;

            return ReturnsValue(falseTarget) && IsSideEffectArm(trueTarget) &&
                       trueTarget.To.Contains(falseTarget) &&
                       OwnsReturn(falseTarget, trueTarget) ||
                   ReturnsValue(trueTarget) && IsSideEffectArm(falseTarget) &&
                       falseTarget.To.Contains(trueTarget) &&
                       OwnsReturn(trueTarget, falseTarget);
        }

        /// <summary>
        /// 条件链入口有时同时负责初始化局部变量，例如按键分派前的 l/r 赋值。
        /// 这使入口不再满足 Statements.IsCondition()，而后续纯条件根会被内向外
        /// 预处理抢先拆散。若入口支配一个由三条以上成功边共享的真实正文，应先
        /// 从这个带载荷入口恢复整条谓词；替换仅作用于末尾条件，前置赋值保留原位。
        /// </summary>
        private void StructurePayloadRootSharedBodyConditionChains()
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             !candidate.Statements.IsCondition() &&
                                             candidate.Statements.GetCondition() != null &&
                                             candidate.To.Count == 2 &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderBy(candidate => candidate.Start)
                         .ToList())
            {
                var sharedBody = _context.Blocks.FirstOrDefault(candidate =>
                    !candidate.Hidden && candidate != root &&
                    candidate.From.Count >= 3 &&
                    candidate.Dominator != null &&
                    candidate.Dominator[root.Id] &&
                    HasBranchPayload(candidate) &&
                    candidate.Statements.GetCondition() == null);
                if (sharedBody == null)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    TryStructureConditionChain(root, condition, out var chainLogic))
                {
                    root.Statements.Replace(
                        condition, chainLogic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 多段短路判断可能从三条以上路径汇入同一个更新块，而其父条件又位于更大
        /// 的早退区域中。若不先恢复这条链，外围 if 会把各段比较当普通表达式收走。
        /// 仅处理非循环、纯条件入口、共享块带真实载荷且由当前入口支配的形态。
        /// </summary>
        private void StructureNestedSharedBodyConditionChains()
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             candidate.Statements.IsCondition() &&
                                             candidate.From.Count >= 1 &&
                                             (candidate.From.Count > 1 ||
                                              candidate.From[0].Statements.GetCondition() != null) &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                var sharedBody = _context.Blocks.FirstOrDefault(candidate =>
                    !candidate.Hidden && candidate != root &&
                    candidate.From.Count >= 3 &&
                    candidate.Dominator != null &&
                    candidate.Dominator[root.Id] &&
                    HasBranchPayload(candidate) &&
                    candidate.Statements.GetCondition() == null);
                if (sharedBody == null)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    TryStructureConditionChain(root, condition, out var chainLogic))
                {
                    root.Statements.Replace(
                        condition, chainLogic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 局部初始化的 if/else 汇合后，后续长分派入口会有两个普通载荷前驱。
        /// 这种入口必须由外向内恢复；通用的单前驱嵌套链仍保持由内向外，避免
        /// 改变普通早退守卫的公共继续路径。
        /// </summary>
        private void StructureMultiPredecessorSharedBodyConditionChains()
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             candidate.Statements.IsCondition() &&
                                             candidate.From.Count > 1 &&
                                             candidate.From.All(predecessor =>
                                                 predecessor.Statements.GetCondition() == null) &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderBy(candidate => candidate.Start)
                         .ToList())
            {
                var sharedBody = _context.Blocks.FirstOrDefault(candidate =>
                    !candidate.Hidden && candidate != root &&
                    candidate.From.Count >= 3 &&
                    candidate.Dominator != null &&
                    candidate.Dominator[root.Id] &&
                    HasBranchPayload(candidate) &&
                    candidate.Statements.GetCondition() == null);
                if (sharedBody == null)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    TryStructureConditionChain(root, condition, out var logic))
                {
                    root.Statements.Replace(condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 某些很长的对象过滤条件会被字节码拆成“入口条件 + 多段比较 + 共享门”。
        /// 当入口真分支和共享门真分支进入同一主体，而门的假分支直接返回时，可将
        /// 两个已经求出的谓词用 || 合并。这样能避免入口条件在外围 if 中退化为
        /// 无控制作用的裸比较，同时保留门谓词中的函数调用及短路顺序。
        /// </summary>
        private void StructureMergedBodyGateConditions()
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             !candidate.Statements.IsCondition() &&
                                             candidate.Statements.GetCondition() != null &&
                                             candidate.To.Count == 2 &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                var rootCondition = root.Statements.GetCondition();
                var body = root.To.FirstOrDefault(target =>
                    target.Start == rootCondition.TrueBranch);
                var chainStart = root.To.FirstOrDefault(target =>
                    target.Start == rootCondition.FalseBranch);
                if (body == null || chainStart == null)
                {
                    continue;
                }

                var gate = _context.Blocks.FirstOrDefault(candidate =>
                {
                    var gateCondition = candidate.Hidden
                        ? null
                        : candidate.Statements.GetCondition();
                    if (gateCondition == null || candidate.From.Count < 4 ||
                        gateCondition.TrueBranch != body.Start ||
                        !CanReach(chainStart, candidate))
                    {
                        return false;
                    }

                    var exit = candidate.To.FirstOrDefault(target =>
                        target.Start == gateCondition.FalseBranch);
                    return exit?.Statements?.Any(statement =>
                        statement is ReturnExpression) == true;
                });
                if (gate == null)
                {
                    continue;
                }

                var gateCondition = gate.Statements.GetCondition();
                var exitBlock = gate.To.First(target =>
                    target.Start == gateCondition.FalseBranch);

                // 旧的比较链已经完整折叠进共享门谓词；断开并隐藏控制流外壳，
                // 再让普通 if 区域恢复负责主体及公共 return 的归属。
                var obsoleteChain = new HashSet<Block>();
                var pending = new Stack<Block>();
                pending.Push(chainStart);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (current == body || current == exitBlock ||
                        !obsoleteChain.Add(current))
                    {
                        continue;
                    }

                    foreach (var next in current.To)
                    {
                        pending.Push(next);
                    }
                }

                rootCondition.Condition = rootCondition.Condition.Or(
                    gateCondition.Condition);
                rootCondition.JumpIf = true;
                rootCondition.JumpTo = body.Start;
                rootCondition.ElseTo = exitBlock.Start;
                foreach (var oldTarget in root.To)
                {
                    oldTarget.From.Remove(root);
                }
                root.To = new List<Block> { body, exitBlock };
                if (!body.From.Contains(root))
                {
                    body.From.Add(root);
                }
                if (!exitBlock.From.Contains(root))
                {
                    exitBlock.From.Add(root);
                }
                foreach (var obsolete in obsoleteChain)
                {
                    obsolete.Hidden = true;
                }

                if (StructureIfElseCore(
                        root, out var mergedLogic, false, exitBlock))
                {
                    root.Statements.Replace(
                        rootCondition, mergedLogic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 条件跳转的真、假目标完全相同时，条件结果没有控制流用途。纯条件可直接
        /// 删除；若求值中含调用或赋值则只保留该表达式的副作用。否则这种字节码
        /// 会在嵌套块中泄露成 `typeof x == "String";` 一类伪语句。
        /// </summary>
        private void NormalizeNoOpConditions()
        {
            foreach (var block in _context.Blocks)
            {
                if (block.Statements == null)
                {
                    continue;
                }

                for (var index = block.Statements.Count - 1; index >= 0; index--)
                {
                    if (block.Statements[index] is not ConditionExpression condition ||
                        condition.TrueBranch != condition.FalseBranch)
                    {
                        continue;
                    }

                    if (ExpressionEffectAnalysis.HasObservableEffect(condition.Condition))
                    {
                        block.Statements[index] = condition.Condition;
                    }
                    else
                    {
                        block.Statements.RemoveAt(index);
                    }
                }
            }
        }

        /// <summary>
        /// 表达式恢复可能已把短路比较合并进后续 ConditionExpression，但原 CFG
        /// 仍保留一层纯条件菱形。例如两条边分别直达、或经空计算块到达同一后续
        /// 条件时，这一层已不再控制任何语句。若继续按 if 结构化，会生成空 else-if
        /// 并把后续短路项从真实条件中拆掉。这里只隐藏无副作用且两侧归一化目标
        /// 完全相同的外壳；调用、赋值、自增等求值仍原样保留。
        /// </summary>
        private void HideRedundantDecisionShells()
        {
            if (_context.BlockTable == null)
            {
                return;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in _context.Blocks)
                {
                    if (block.Hidden || !block.Statements.IsCondition() || block.To.Count != 2 ||
                        !_context.LoopSet.Any(loop => loop.Contains(block)))
                    {
                        continue;
                    }

                    var condition = block.Statements.GetCondition();
                    if (condition == null ||
                        ExpressionEffectAnalysis.HasObservableEffect(condition.Condition))
                    {
                        continue;
                    }

                    var passthrough = new HashSet<Block>();
                    var normalizedTrue = NormalizeCollapsedDecisionTarget(
                        _context.BlockTable[condition.TrueBranch], passthrough);
                    var normalizedFalse = NormalizeCollapsedDecisionTarget(
                        _context.BlockTable[condition.FalseBranch], passthrough);
                    if (normalizedTrue != normalizedFalse || passthrough.Count == 0)
                    {
                        continue;
                    }

                    block.Statements.Remove(condition);
                    block.Hidden = true;
                    foreach (var passthroughBlock in passthrough)
                    {
                        passthroughBlock.Hidden = true;
                    }
                    changed = true;
                }
            }
        }

        private void HideCollapsedPhiConditions()
        {
            if (_context.BlockTable == null)
            {
                return;
            }

            // 条件两侧都只是给 Phi 提供值并立即汇合时，表达式传播已经把它
            // 折叠为三元表达式。必须在循环体结构化前移除控制流外壳，否则
            // 其中一侧可能被误认成 continue，真正的赋值反而被跳过。
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in _context.Blocks)
                {
                    var condition = block.Hidden ? null : block.Statements.GetCondition();
                    if (condition == null ||
                        !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                        !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
                    {
                        continue;
                    }

                    var passthrough = new HashSet<Block>();
                    var hasLeadingPayload = !block.Statements.IsCondition();
                    var hasLeadingSelfAssignment = block.Statements.Any(statement =>
                        statement is BinaryExpression binary && binary.Op.CanSelfAssign() &&
                        binary.Left is LocalExpression);

                    var normalizedTrue = hasLeadingPayload
                        ? NormalizeCollapsedDecisionTarget(trueTarget, passthrough)
                        : NormalizeDecisionTarget(trueTarget, passthrough);
                    var normalizedFalse = hasLeadingPayload
                        ? NormalizeCollapsedDecisionTarget(falseTarget, passthrough)
                        : NormalizeDecisionTarget(falseTarget, passthrough);
                    if (normalizedTrue != normalizedFalse || passthrough.Count == 0)
                    {
                        continue;
                    }

                    var hasConditionalPhiAssignment =
                        normalizedTrue.Statements.OfType<BinaryExpression>().Any(binary =>
                            binary.Op == BinaryOp.Assign &&
                            ContainsConditionalPhi(binary.Right));
                    if (hasLeadingPayload && !hasLeadingSelfAssignment &&
                        !hasConditionalPhiAssignment)
                    {
                        continue;
                    }

                    // 带前置语句的扩展形态只处理已折叠进 return 的短路值，或
                    // 后继中已经形成条件 Phi 赋值的三元式。其他普通分支仍交给
                    // 原有控制流恢复，否则可能把比较结果误当左值。
                    if (hasLeadingPayload &&
                        !hasConditionalPhiAssignment &&
                        !normalizedTrue.Statements.OfType<ReturnExpression>()
                            .Any(returnExpression => returnExpression.Return != null))
                    {
                        continue;
                    }

                    if (!hasLeadingPayload)
                    {
                        // 普通纯条件块隐藏但保留条件节点，供后续迭代穿透更外层
                        // 折叠图。若后继已包含条件 Phi，条件求值已经进入三元式，
                        // 必须删除原节点，避免它被再次结构化成空 if 并抢先隐藏主体。
                        if (hasConditionalPhiAssignment)
                        {
                            block.Statements.Remove(condition);
                        }
                        block.Hidden = true;
                    }
                    else
                    {
                        // 条件前可能还有 `value += step` 一类真实语句，不能连同
                        // 条件壳一起隐藏。条件若有副作用则保留其求值，否则删除。
                        if (!hasConditionalPhiAssignment &&
                            ExpressionEffectAnalysis.HasObservableEffect(
                                condition.Condition))
                        {
                            block.Statements.Replace(condition, condition.Condition);
                        }
                        else
                        {
                            block.Statements.Remove(condition);
                        }

                        block.Hidden = block.Statements.Count == 0;
                    }
                    foreach (var passthroughBlock in passthrough)
                    {
                        passthroughBlock.Hidden = true;
                    }
                    changed = true;
                }
            }
        }

        /// <summary>
        /// 三元返回的条件可能仍引用一个在前驱块中赋值的临时值，例如
        /// `name != "" &amp;&amp; (index = text.indexOf(name)) &gt; 0`。Phi 已恢复出
        /// 三元式后，把赋值按右侧依赖内联回对应条件，随后原条件 CFG 才能安全隐藏。
        /// </summary>
        private void InlineAssignmentsIntoConditionalPhiGuards()
        {
            var phis = new List<(PhiExpression Phi, Block ReturnBlock)>();
            void CollectPhis(Expression expression, Block returnBlock)
            {
                if (expression == null)
                {
                    return;
                }

                if (expression is PhiExpression phi && phi.IsConditional)
                {
                    phis.Add((phi, returnBlock));
                    CollectPhis(phi.ThenBranch, returnBlock);
                    CollectPhis(phi.ElseBranch, returnBlock);
                    return;
                }

                foreach (var child in expression.Children?.OfType<Expression>() ??
                                      Enumerable.Empty<Expression>())
                {
                    CollectPhis(child, returnBlock);
                }
            }

            foreach (var returnBlock in _context.Blocks)
            {
                foreach (var ret in (returnBlock.Statements ?? new List<IAstNode>())
                             .OfType<ReturnExpression>())
                {
                    CollectPhis(ret.Return, returnBlock);
                }
            }
            if (phis.Count == 0)
            {
                return;
            }

            static HashSet<int> LocalSlots(Expression expression)
            {
                var result = new HashSet<int>();
                void Visit(Expression current)
                {
                    if (current == null)
                    {
                        return;
                    }
                    if (current is LocalExpression local)
                    {
                        result.Add(local.Slot);
                    }

                    IEnumerable<Expression> Children(Expression value) => value switch
                    {
                        InvokeExpression invoke => new[]
                            {
                                invoke.Instance, invoke.MethodExpression
                            }.Where(child => child != null).Concat(invoke.Parameters),
                        BinaryExpression binary => new[] { binary.Left, binary.Right },
                        UnaryExpression unary => new[] { unary.Target },
                        ConditionExpression conditional => new[] { conditional.Condition },
                        PropertyAccessExpression property => new[]
                            { property.Instance, property.Property },
                        PhiExpression phi when phi.IsConditional => new[]
                            { phi.Condition?.Condition, phi.ThenBranch, phi.ElseBranch }
                            .Where(child => child != null),
                        _ => value.Children?.OfType<Expression>() ??
                             Enumerable.Empty<Expression>()
                    };
                    foreach (var child in Children(current))
                    {
                        Visit(child);
                    }
                }
                Visit(expression);
                return result;
            }

            var affectedReturns = new HashSet<Block>();
            foreach (var block in _context.Blocks.OrderBy(candidate => candidate.Start))
            {
                if (block.Statements.Count != 2 ||
                    block.Statements[0] is not BinaryExpression assignment ||
                    assignment.Op != BinaryOp.Assign ||
                    assignment.Left is not LocalExpression target ||
                    block.Statements[1] is not ConditionExpression condition ||
                    !ReferencesAssignmentTarget(condition, assignment.Left))
                {
                    continue;
                }

                var rightSlots = LocalSlots(assignment.Right);
                var matching = phis.FirstOrDefault(entry =>
                {
                    var conditionSlots = LocalSlots(entry.Phi.Condition.Condition);
                    return conditionSlots.Contains(target.Slot) &&
                           rightSlots.Any(conditionSlots.Contains);
                });
                if (matching.Phi == null)
                {
                    continue;
                }

                var replaced = false;
                matching.Phi.Condition.Condition = ReplaceConditionTarget(
                    matching.Phi.Condition.Condition, assignment.Left, assignment, ref replaced);
                if (!replaced)
                {
                    continue;
                }

                assignment.IsDeclaration = false;
                block.Statements.RemoveAt(0);
                affectedReturns.Add(matching.ReturnBlock);
                phis.Remove(matching);
            }

            // 赋值已进入三元条件，沿途的条件/跳转块只剩控制流外壳。保留入口
            // 的变量声明和最终 return，清除中间外壳，防止后续再次恢复成 if。
            foreach (var returnBlock in affectedReturns)
            {
                foreach (var block in _context.Blocks.Where(candidate =>
                             candidate != returnBlock &&
                             candidate.Start < returnBlock.Start &&
                             CanReach(candidate, returnBlock)))
                {
                    block.Statements.RemoveAll(statement =>
                        statement is ConditionExpression or GotoExpression);
                    block.Hidden = block.Statements.Count == 0;
                }
                returnBlock.Hidden = false;
            }
        }

        private static bool ContainsConditionalPhi(Expression expression)
        {
            if (expression is PhiExpression { IsConditional: true })
            {
                return true;
            }

            // 多数表达式节点尚未实现 Children；显式遍历常见容器，尤其是
            // return Dictionary(... ternary ...) 这类条件 Phi 位于调用参数中的形态。
            return expression switch
            {
                ReturnExpression ret => ContainsConditionalPhi(ret.Return),
                InvokeExpression invoke => ContainsConditionalPhi(invoke.Instance) ||
                                           ContainsConditionalPhi(invoke.MethodExpression) ||
                                           invoke.Parameters.Any(ContainsConditionalPhi),
                BinaryExpression binary => ContainsConditionalPhi(binary.Left) ||
                                           ContainsConditionalPhi(binary.Right),
                UnaryExpression unary => ContainsConditionalPhi(unary.Target),
                ConditionExpression condition => ContainsConditionalPhi(condition.Condition),
                PropertyAccessExpression property => ContainsConditionalPhi(property.Instance) ||
                                                     ContainsConditionalPhi(property.Property),
                _ => expression?.Children?.OfType<Expression>()
                    .Any(ContainsConditionalPhi) == true
            };
        }

        /// <summary>
        /// 三元 Phi 已进入 return/调用参数后，产生它的条件菱形只剩隐藏外壳。
        /// 外层 if 的单侧 CFG 边仍指向该外壳时，可穿透到真正的返回块；仅在目标
        /// 确实含条件 Phi 时归一化，避免改变默认参数初始化等普通隐藏条件。
        /// </summary>
        private Block NormalizeConsumedConditionalPhiTarget(Block target)
        {
            if (target == null || !target.Hidden)
            {
                return target;
            }

            var passthrough = new HashSet<Block>();
            // HideCollapsedPhiConditions 会在确认条件已进入 Phi 后删除条件节点，
            // 因而这里既要穿透“仍有条件的隐藏菱形”，也要穿透“已清空的隐藏菱形”。
            var normalized = target.Statements.GetCondition() != null
                ? NormalizeCollapsedDecisionTarget(target, passthrough)
                : NormalizeDecisionTarget(target, passthrough);
            if (normalized == target ||
                !normalized.Statements.OfType<Expression>().Any(ContainsConditionalPhi))
            {
                return target;
            }

            foreach (var block in passthrough)
            {
                block.Hidden = true;
            }
            return normalized;
        }

        /// <summary>
        /// 沿无副作用的纯条件块和空跳板寻找最终目标。表达式传播把短路结果折叠
        /// 进后继表达式后，原 CFG 仍会留下这类条件外壳；仅在所有路径汇到同一
        /// 目标时才穿透，避免把真正控制副作用的 if 当成可删除条件。
        /// </summary>
        private static Block NormalizeCollapsedDecisionTarget(
            Block block, HashSet<Block> passthroughBlocks)
        {
            var memo = new Dictionary<Block, Block>();
            var visiting = new HashSet<Block>();

            Block Normalize(Block current)
            {
                if (current == null || !visiting.Add(current))
                {
                    return current;
                }

                if (memo.TryGetValue(current, out var cached))
                {
                    visiting.Remove(current);
                    return cached;
                }

                var result = current;
                if ((current.Statements.Count == 0 || IsDecisionPassthrough(current)) &&
                    current.To.Count == 1)
                {
                    passthroughBlocks?.Add(current);
                    result = Normalize(current.To[0]);
                }
                else if (current.Statements.IsCondition() && current.To.Count == 2)
                {
                    var condition = current.Statements.GetCondition();
                    if (!ExpressionEffectAnalysis.HasObservableEffect(
                            condition?.Condition))
                    {
                        var normalizedFirst = Normalize(current.To[0]);
                        var normalizedSecond = Normalize(current.To[1]);
                        if (normalizedFirst == normalizedSecond)
                        {
                            passthroughBlocks?.Add(current);
                            result = normalizedFirst;
                        }
                    }
                }

                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Normalize(block);
        }

        /// <summary>
        /// Structure if-else statements within loop body
        /// </summary>
        private void StructureLoopBodyIfElse(List<Block> bodyBlocks)
        {
            if (bodyBlocks == null) return;

            var bodySet = bodyBlocks.ToHashSet();

            // 循环中的 switch 常紧跟在 break/continue 守卫之后。先按完整分派
            // 恢复，避免外围守卫的支配区域把 case 链吸收成带重复路径条件的 if。
            StructureEqualityDispatches(bodyBlocks);

            // 先从最早入口恢复最终 return 的整条短路链。入口本身未必直接连到
            // return（如 `(ext != "" && match) || (ext == "" && probe())`），
            // 只寻找“纯条件/跳板路径”可达循环外 return 的根，避免改写普通 if。
            // 这一步必须早于局部两段守卫；局部计划一旦先把内层条件物化成 AST，
            // 外层就无法再从原始 CFG 合成所有成功路径的完整谓词。
            foreach (var root in bodyBlocks
                         .Where(block => !block.Hidden && block.Statements.GetCondition() != null &&
                                         CanReachExternalReturnThroughConditions(block, bodySet))
                         .OrderBy(block => block.Start)
                         .ToList())
            {
                if (root.Hidden)
                {
                    continue;
                }

                var rootCondition = root.Statements.GetCondition();
                if (TryStructureConditionChain(root, rootCondition, out var returnChain))
                {
                    root.Statements.Replace(
                        rootCondition, returnChain.Simplify().ToStatement());
                }
            }

            StructureTwoStageGuardedPayloads(bodyBlocks);

            // 先恢复直接离开自然循环的 return 守卫。外围 case/范围条件若先物化，
            // 会把尚未结构化的最后一项（常见于 switch 的最终 case）吞成空分支。
            foreach (var guard in bodyBlocks
                         .Where(block => !block.Hidden && block.Statements.GetCondition() != null &&
                                         block.To.Any(target => !bodySet.Contains(target) &&
                                             target.Statements.Any(node => node is ReturnExpression)))
                         .OrderBy(block => block.Start)
                         .ToList())
            {
                if (guard.Hidden)
                {
                    continue;
                }

                var guardCondition = guard.Statements.GetCondition();
                // 多个条件共享同一个 return 时必须从最早入口整体恢复；先物化
                // 尾部条件会隐藏共享 return，使前面的真分支退化为空块。
                if (TryStructureConditionChain(guard, guardCondition, out var guardChain))
                {
                    guard.Statements.Replace(
                        guardCondition, guardChain.Simplify().ToStatement());
                }
                else if (StructureIfElse(guard, out var guardLogic))
                {
                    guard.Statements.Replace(
                        guardCondition, guardLogic.Simplify().ToStatement());
                    guardLogic.HideBlocks(false);
                }
            }

            // 先处理两侧分别以 ++/-- 开始的方向菱形。循环头守卫若先物化，
            // 会把它整体隐藏；匹配必须足够严格，不能把普通比较链倒序物化。
            foreach (var diamond in bodyBlocks
                         .Where(block => !block.Hidden && block.To.Count == 2 &&
                                         block.Statements.GetCondition() != null &&
                                         block.To.All(target =>
                                             target.Statements.FirstOrDefault() is UnaryExpression unary &&
                                             unary.Op is UnaryOp.Inc or UnaryOp.Dec))
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                var diamondCondition = diamond.Statements.GetCondition();
                if (StructureIfElse(diamond, out var diamondLogic))
                {
                    diamond.Statements.Replace(
                        diamondCondition, diamondLogic.Simplify().ToStatement());
                    diamondLogic.HideBlocks(false);
                }
            }

            // Process blocks multiple times to handle nested structures properly
            bool changed = true;
            int maxIterations = 5;
            int iteration = 0;
            while (changed && iteration < maxIterations)
            {
                changed = false;
                iteration++;

                for (int i = 0; i < bodyBlocks.Count; i++)
                {
                    var block = bodyBlocks[i];
                    // Skip blocks that are already hidden
                    if (block.Hidden) continue;

                    var originalCondition = block.Statements.GetCondition();
                    if (StructureIfElse(block, out var logic))
                    {
                        block.Statements.Replace(originalCondition, logic.Simplify().ToStatement());
                        // Hide the blocks that are part of the if-else structure
                        logic.HideBlocks(false); // Don't hide the condition block itself
                        changed = true;
                    }
                }
            }
        }

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

                var bodyRegion = body.To.Contains(skip)
                    ? new List<Block> { body }
                    : CollectDominatedBranchRegion(root, body, skip, true);
                if (bodyRegion.Count == 0 ||
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

        private void BuildTry(System.Func<Block, bool> entryFilter = null)
        {
            List<Block> SelectBlocksInRange(int startInclusive, int endExclusive)
            {
                return _context.Blocks
                    .Where(b => b.Start >= startInclusive && b.Start < endExclusive)
                    .OrderBy(b => b.Start)
                    .ToList();
            }

            List<Block> SelectReachableBlocks(Block entry, Block exit)
            {
                var selected = new HashSet<Block>();
                var pending = new Stack<Block>();
                pending.Push(entry);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (current == null || current == exit ||
                        current.Start < entry.Start || current.Start >= exit.Start ||
                        !selected.Add(current))
                    {
                        continue;
                    }

                    foreach (var next in current.To)
                    {
                        pending.Push(next);
                    }
                }

                return selected.OrderBy(candidate => candidate.Start).ToList();
            }

            Block GetJumpTarget(Block b)
            {
                var last = b.Instructions.LastOrDefault();
                if (last == null || last.OpCode != OpCode.JMP)
                {
                    return null;
                }

                var jumpData = last.Data as JumpData;
                if (jumpData == null)
                {
                    return null;
                }

                return _context.BlockTable.TryGetValue(jumpData.Goto.Line, out var target) ? target : null;
            }

            Block FindTryEndByExtryJump(Block enterTry, Block catchOrExitTry)
            {
                return _context.Blocks
                    .Where(b => b.Start > enterTry.Start && b.Start < catchOrExitTry.Start)
                    .Where(b => b.Instructions.Any(i => i.OpCode == OpCode.EXTRY))
                    .Select(GetJumpTarget)
                    .Where(target => target != null && target.Start > catchOrExitTry.Start)
                    .OrderBy(target => target.Start)
                    .FirstOrDefault();
            }

            Block FindTryEnd(Block startTry, Block catchOrExitTry, Block current)
            {
                if (current.Start <= startTry.Start)
                {
                    return null;
                }

                if (current.Instructions.Any(i => i.OpCode == OpCode.EXTRY))
                {
                    var lastIns = current.Instructions.LastOrDefault();
                    if (lastIns is { OpCode: OpCode.JMP })
                    {
                        var target = current.To.First();
                        if (target.Start >= catchOrExitTry.Start)
                        {
                            return target;
                        }
                    }
                }

                foreach (var next in current.From)
                {
                    var b = FindTryEnd(startTry, catchOrExitTry, next);
                    if (b != null)
                    {
                        return b;
                    }
                }

                return null;
            }

            var entryBlocks = _context.Blocks
                .Where(b => b.Instructions.LastOrDefault()?.OpCode == OpCode.ENTRY)
                .Where(b => entryFilter == null || entryFilter(b))
                .OrderByDescending(b => b.Start)
                .ToList();

            foreach (var block in entryBlocks)
            {
                TryLogic t = new TryLogic();
                t.EnterTry = block;
                t.CatchClause = (Expression)block.Statements.LastOrDefault(stmt => stmt is CatchExpression);
                var catchOrExitTry = _context.BlockTable[((JumpData)block.Instructions.Last().Data).Goto.Line];
                var tryEnd = FindTryEndByExtryJump(block, catchOrExitTry) ?? FindTryEnd(block, catchOrExitTry, catchOrExitTry);
                t.ExitTry = tryEnd ?? catchOrExitTry;
                var containingLoop = _context.LoopSet
                    .Where(loop => loop.Contains(block))
                    .OrderBy(loop => loop.Blocks.Count)
                    .FirstOrDefault();
                var containingLoopExit = containingLoop?.Break ??
                                         (containingLoop?.LoopLogic as DoWhileLogic)?.Break;
                t.ExitTryBreaksLoop = containingLoopExit != null &&
                                      t.ExitTry == containingLoopExit;
                t.Body = SelectBlocksInRange(block.Start + 1, catchOrExitTry.Start);
                if (tryEnd != null && tryEnd != catchOrExitTry) // has catch
                {
                    // catch 后面常紧接外围 else-if 的下一项，但 catch 自己会直接
                    // 跳到 try 的公共出口。按地址范围收集会把这些不可达分支全部
                    // 吞进 catch；应只沿 catch 入口实际可达的 CFG 边收集。
                    t.CatchBody = SelectReachableBlocks(catchOrExitTry, tryEnd);
                }

                // 在 try/catch 体内结构化 if-else（需在 ToStatement 之前，
                // 因为 ToStatement 会隐藏体内块并过滤掉未结构化的 ConditionExpression）
                StructureLoopBodyIfElse(t.Body);
                if (t.CatchBody != null)
                    StructureLoopBodyIfElse(t.CatchBody);

                var tryStatement = t.ToStatement();
                if (t.CatchClause != null && block.Statements.Contains(t.CatchClause))
                {
                    block.Statements.Replace(t.CatchClause, tryStatement);
                }
                else
                {
                    block.Statements.Insert(0, tryStatement);
                }
            }
        }

        public Expression FindCondition(Loop l)
        {
            if (l.Blocks.Count <= 0)
            {
                return null;
            }

            var last = l.Blocks.Last();
            var exps = last.Statements;
            if (exps == null || exps.Count <= 0)
            {
                return null;
            }

            return (Expression) exps.LastOrDefault(s => s is BinaryExpression b && b.IsCompare);
        }

        private Block FindBreak(Loop loop)
        {
            BitArray b = new BitArray(_context.ExitBlock.PostDominator.Length);
            b.SetAll(true);
            foreach (var block in loop.Blocks)
            {
                b.And(block.PostDominator);
                b[block.Id] = false; //block after break can not still stay in the loop
            }

            var id = b.FirstIndexOf(true);
            if (id >= 0)
            {
                return _context.Blocks[id];
            }

            return null;
        }

        internal void IntervalAnalysisDoWhilePass()
        {
            _context.LoopSet.ForEach(l => l.Blocks.Sort((b1, b2) => b1.Start - b2.Start));

            foreach (var loop in _context.LoopSet)
            {
                var lastBlock = loop.Blocks.Last();
                var dw = new DoWhileLogic();
                dw.Break = FindBreak(loop);

                Block conditionBlock = null;
                var mergedTailPrefixes = new List<(Block Block, ConditionExpression Condition)>();
                var headerCondition = loop.Header.Statements.GetCondition();
                var tailCondition = lastBlock.Statements.GetCondition();
                Block headerTrue = null;
                Block headerFalse = null;
                var headerIsEntryTest = headerCondition != null &&
                    _context.BlockTable != null &&
                    _context.BlockTable.TryGetValue(headerCondition.TrueBranch, out headerTrue) &&
                    _context.BlockTable.TryGetValue(headerCondition.FalseBranch, out headerFalse) &&
                    loop.Contains(headerTrue) != loop.Contains(headerFalse);
                var headerHasOnlyConditionPreparation = headerCondition != null &&
                    loop.Header.Statements.All(statement =>
                        ReferenceEquals(statement, headerCondition) ||
                        statement is UnaryExpression unary &&
                        unary.Op is UnaryOp.Inc or UnaryOp.Dec &&
                        ReferencesAssignmentTarget(headerCondition, unary.Target) ||
                        statement is BinaryExpression assignment &&
                         assignment.Op == BinaryOp.Assign &&
                         ReferencesAssignmentTarget(headerCondition, assignment.Left));
                var headerStartsWithFirstDeclaration = headerCondition != null &&
                    loop.Header.Statements.OfType<BinaryExpression>().Any(assignment =>
                        assignment.Op == BinaryOp.Assign &&
                        assignment.IsDeclaration &&
                        ReferencesAssignmentTarget(headerCondition, assignment.Left) &&
                        !HasEarlierDominatingDeclaration(loop.Header, assignment));
                Block tailExitTarget = null;
                if (tailCondition is ConditionExpression tailBackCondition)
                {
                    var tailExitStart = tailBackCondition.TrueBranch == loop.Header.Start
                        ? tailBackCondition.FalseBranch
                        : tailBackCondition.FalseBranch == loop.Header.Start
                            ? tailBackCondition.TrueBranch
                            : -1;
                    if (tailExitStart >= 0)
                    {
                        _context.BlockTable?.TryGetValue(tailExitStart, out tailExitTarget);
                    }
                }
                var tailHasDistinctBreakPayload = tailExitTarget != null &&
                    dw.Break != null && tailExitTarget != dw.Break &&
                    CanReach(tailExitTarget, dw.Break);

                // while/for 的入口条件块有时以 ++i 之类的求值准备开头，而循环
                // 体末尾又恰好是“命中后执行载荷再退出，否则回头”的条件。
                // 此时末块虽有回边，却不是 do-while 条件；优先保留真正的入口测试。
                // 但循环头若首次声明条件使用的局部值，该赋值必须每轮作为循环体执行，
                // 不能提升成 while 条件；这种形态应由真正的尾部回边恢复为 do-while。
                if (headerIsEntryTest && headerHasOnlyConditionPreparation &&
                    !headerStartsWithFirstDeclaration && tailHasDistinctBreakPayload)
                {
                    var trueIsBody = loop.Contains(headerTrue);
                    dw.IsWhile = true;
                    dw.Condition = trueIsBody
                        ? headerCondition
                        : (ConditionExpression)headerCondition.Invert();
                    dw.Break = trueIsBody ? headerFalse : headerTrue;
                    conditionBlock = loop.Header;
                }
                else if (tailCondition is ConditionExpression lastCond &&
                    (lastCond.TrueBranch == loop.Header.Start ||
                     lastCond.FalseBranch == loop.Header.Start))
                {
                    // 尾部测试优先于头部的早退条件。do-while 的循环体开头常有
                    // `if (...) return`，若先看头部会把早退误认成 while 条件。
                    dw.Condition = lastCond.TrueBranch == loop.Header.Start
                        ? lastCond
                        : (ConditionExpression)lastCond.Invert();
                    var exitStart = lastCond.TrueBranch == loop.Header.Start
                        ? lastCond.FalseBranch
                        : lastCond.TrueBranch;
                    if (_context.BlockTable != null &&
                        _context.BlockTable.TryGetValue(exitStart, out var tailExit))
                    {
                        dw.Break = tailExit;
                    }
                    conditionBlock = lastBlock;

                    // 复合尾条件的前缀不一定在循环头。先从真正负责回边的闩锁
                    // 向前收集所有“成功进入下一项、失败进入同一出口”的条件块。
                    if (dw.Break != null &&
                        LoopTailConditionAnalyzer.TryAnalyze(
                            loop, lastBlock, dw.Break, dw.Condition,
                            out var tailPlan))
                    {
                        dw.Condition = tailPlan.Condition;
                        mergedTailPrefixes.AddRange(tailPlan.Prefixes);
                    }

                    // do-while 的复合条件会被编译成“带循环体尾语句的首个条件 +
                    // 纯条件闩锁块”。例如 `c < n && colors[c] == ""` 中，前半段
                    // 留在 Header，后半段才负责回跳。两段的失败出口相同且 Header
                    // 的成功分支直达闩锁时，可以安全恢复为一个短路 && 条件。
                    if (loop.Header != lastBlock && headerCondition != null &&
                        ((headerCondition.TrueBranch == lastBlock.Start &&
                          headerCondition.FalseBranch == exitStart) ||
                         (headerCondition.FalseBranch == lastBlock.Start &&
                          headerCondition.TrueBranch == exitStart)))
                    {
                        var prefix = headerCondition.TrueBranch == lastBlock.Start
                            ? headerCondition.Condition
                            : headerCondition.Condition.Invert();
                        dw.Condition = prefix.And(dw.Condition);
                        mergedTailPrefixes.Add((loop.Header, headerCondition));
                    }
                }
                else if (headerIsEntryTest)
                {
                    // 入口测试型循环：一个分支进入循环体，另一个分支离开循环。
                    // 不能依赖公共后支配块来判断退出目标，因为退出分支可能直接 return。
                    var trueIsBody = loop.Contains(headerTrue);
                    dw.IsWhile = true;
                    dw.Condition = trueIsBody
                        ? headerCondition
                        : (ConditionExpression)headerCondition.Invert();
                    dw.Break = trueIsBody ? headerFalse : headerTrue;
                    conditionBlock = loop.Header;
                }
                else if (lastBlock.Statements.Count == 1 && lastBlock.Statements[0] is GotoExpression &&
                         loop.Header.Statements.GetCondition() is ConditionExpression cond &&
                         cond.FalseBranch == dw.Break.Start)
                {
                    dw.IsWhile = true;
                    dw.Condition = cond;
                    conditionBlock = loop.Header;
                }
                dw.Body = new List<Block>(loop.Blocks);
                // Remove condition block from body if it's not part of the loop body
                if (conditionBlock != null && conditionBlock != loop.Header)
                {
                    dw.Body.Remove(conditionBlock);
                }
                foreach (var (prefixBlock, prefixCondition) in mergedTailPrefixes)
                {
                    prefixBlock.Statements.Remove(prefixCondition);
                }

                if (dw.IsWhile && conditionBlock == loop.Header)
                {
                    var loopCondition = dw.Condition;
                    InlineLoopHeaderAssignment(conditionBlock, ref loopCondition);
                    dw.Condition = loopCondition;
                }

                var conditionJump = conditionBlock?.Statements.LastOrDefault(stmt => stmt is IJump);
                dw.Continue = null;

                var cont = loop.Blocks.LastOrDefault();

                if (cont != null)
                {
                    if (cont.Statements.LastOrDefault() is ConditionExpression i)
                    {
                        if (i.JumpTo == loop.Header.Start)
                        {
                            dw.Continue = cont;
                        }
                    }

                    if (cont.Statements.LastOrDefault() is GotoExpression g)
                    {
                        if (g.JumpTo == loop.Header.Start)
                        {
                            dw.Continue = cont;
                        }
                    }
                }

                ILogical logic = dw;
                if (DoWhileToFor(loop, dw, out var f))
                {
                    logic = f;
                }
                else
                {
                    conditionBlock?.Statements.Remove(conditionJump);
                    // while 形式若保留了带实际步进语句的闩锁块，跳到该块只是
                    // 完成本轮 switch/分支并执行步进，并非源码级 continue。
                    // 将它输出成 continue 会直接越过仍在循环体尾部的步进语句。
                    var syntaxContinue = dw.Continue != null &&
                                         dw.Continue.Statements.All(statement =>
                                             statement is IJump)
                        ? dw.Continue
                        : null;
                    StructureLoopTransfers(loop, dw.Body, syntaxContinue, dw.Break);
                }

                loop.LoopLogic = logic;
                loop.Break = dw.Break;
            }
        }

        private static bool CanReachExternalReturnThroughConditions(
            Block start, ISet<Block> loopBody)
        {
            var pending = new Stack<Block>();
            var visited = new HashSet<Block>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current != start && current.Statements.GetCondition() == null &&
                    !IsDecisionPassthrough(current))
                {
                    continue;
                }

                foreach (var next in current.To)
                {
                    if (!loopBody.Contains(next) &&
                        next.Statements.Any(statement => statement is ReturnExpression))
                    {
                        return true;
                    }

                    if (next.Statements.GetCondition() != null || IsDecisionPassthrough(next))
                    {
                        pending.Push(next);
                    }
                }
            }

            return false;
        }

        private void InlineLoopHeaderAssignment(Block conditionBlock, ref Expression condition)
        {
            if (conditionBlock?.Statements == null || condition == null)
            {
                return;
            }

            var currentCondition = condition;
            var conditionIndex = conditionBlock.Statements.FindIndex(node => ReferenceEquals(node, currentCondition));
            if (conditionIndex < 0)
            {
                conditionIndex = conditionBlock.Statements.FindIndex(node => node is ConditionExpression);
            }

            if (conditionIndex <= 0)
            {
                return;
            }

            // `while ((value = next()) !== void)` 会编译成循环头中的赋值语句和
            // 随后的比较。若直接把比较拿去当 while 条件，赋值会落入循环体，首次
            // 检查就读取尚未更新的 value。仅在条件确实读取同一赋值目标时内联。
            for (var i = conditionIndex - 1; i >= 0; i--)
            {
                if (conditionBlock.Statements[i] is UnaryExpression unary &&
                    unary.Op is UnaryOp.Inc or UnaryOp.Dec &&
                    ReferencesAssignmentTarget(condition, unary.Target))
                {
                    // 指令先完成增减、下一条比较再读取新值；内联到条件后必须使用
                    // 前置形式。保留默认后置形式会把 `++i < n` 改成 `i++ < n`。
                    unary.IsPrefix = true;
                    var unaryReplaced = false;
                    condition = ReplaceConditionTarget(
                        condition, unary.Target, unary, ref unaryReplaced);
                    if (unaryReplaced)
                    {
                        conditionBlock.Statements.RemoveAt(i);
                    }
                    return;
                }

                if (conditionBlock.Statements[i] is not BinaryExpression assignment ||
                    assignment.Op != BinaryOp.Assign ||
                    !ReferencesAssignmentTarget(condition, assignment.Left))
                {
                    continue;
                }

                if (assignment.IsDeclaration)
                {
                    var hasEarlierDeclaration =
                        HasEarlierDominatingDeclaration(conditionBlock, assignment);
                    if (!hasEarlierDeclaration)
                    {
                        // 首次声明属于循环体本身，不能生成非法的 `while (var x = ...)`，
                        // 也不能把声明提升出每轮执行的位置。
                        continue;
                    }

                    assignment.IsDeclaration = false;
                }

                var replaced = false;
                condition = ReplaceConditionTarget(condition, assignment.Left, assignment, ref replaced);
                if (replaced)
                {
                    conditionBlock.Statements.RemoveAt(i);
                }
                return;
            }
        }

        private bool HasEarlierDominatingDeclaration(
            Block block, BinaryExpression assignment)
        {
            if (block == null || assignment == null)
            {
                return false;
            }

            return _context.Blocks.Any(candidate =>
                candidate != block && candidate.Start < block.Start &&
                block.Dominator != null && block.Dominator[candidate.Id] &&
                candidate.Statements.OfType<BinaryExpression>().Any(previous =>
                    previous.IsDeclaration && previous.Op == BinaryOp.Assign &&
                    IsSameAssignmentTarget(previous.Left, assignment.Left)));
        }

        /// <summary>
        /// 短路链中允许一种严格的带载荷条件块：一条赋值紧跟一条读取同一目标的
        /// 条件。将纯右值赋值内联后，`ui = source; ui === void` 可恢复为
        /// `(ui = source) === void`，该块也就能继续参与前后的 `||`/`&&` 合并。
        /// 这一等价关系只对 VM 局部槽成立：对象成员的“写入后再读取”可能
        /// 分别触发 setter/getter，不能用赋值表达式的右值代替后一次读取。
        /// 调用、修改型右值等可观察求值不能内联：路径谓词可能在共享决策图中复制该子式，
        /// 使 VM 原本只求值一次的载荷变成多次。此时保留独立赋值，由普通区域
        /// 结构化保存单次求值和后续所有分支。
        /// </summary>
        private void InlineShortCircuitAssignment(Block conditionBlock)
        {
            if (conditionBlock?.Statements?.Count != 2 ||
                conditionBlock.Statements[0] is not BinaryExpression assignment ||
                assignment.Op != BinaryOp.Assign ||
                assignment.Left is not LocalExpression ||
                conditionBlock.Statements[1] is not ConditionExpression condition ||
                !ReferencesAssignmentTarget(condition, assignment.Left) ||
                ExpressionEffectAnalysis.HasObservableEffect(assignment.Right))
            {
                return;
            }

            Expression mergedCondition = condition;
            InlineLoopHeaderAssignment(conditionBlock, ref mergedCondition);
        }

        private static Expression ReplaceConditionTarget(Expression expression, Expression target,
            Expression replacement, ref bool replaced)
        {
            if (expression == null || replaced)
            {
                return expression;
            }

            if (IsSameAssignmentTarget(expression, target))
            {
                replaced = true;
                return replacement;
            }

            switch (expression)
            {
                case ConditionExpression conditional:
                    conditional.Condition = ReplaceConditionTarget(conditional.Condition, target, replacement, ref replaced);
                    if (conditional.Condition != null)
                    {
                        conditional.Condition.Parent = conditional;
                    }
                    break;
                case BinaryExpression binary:
                    binary.Left = ReplaceConditionTarget(binary.Left, target, replacement, ref replaced);
                    if (binary.Left != null)
                    {
                        binary.Left.Parent = binary;
                    }
                    if (!replaced)
                    {
                        binary.Right = ReplaceConditionTarget(binary.Right, target, replacement, ref replaced);
                        if (binary.Right != null)
                        {
                            binary.Right.Parent = binary;
                        }
                    }
                    break;
                case UnaryExpression unary:
                    unary.Target = ReplaceConditionTarget(unary.Target, target, replacement, ref replaced);
                    if (unary.Target != null)
                    {
                        unary.Target.Parent = unary;
                    }
                    break;
            }

            return expression;
        }

        internal bool DoWhileToFor(Loop loop, DoWhileLogic dw, out ForLogic f)
        {
            f = null;

            // 无闩锁块的循环没有可提取的增量表达式，不能转换为 for。
            // 直接按 while/do-while 保留，避免复杂控制流上解引用空 Continue。
            if (dw.Continue == null)
            {
                return false;
            }

            // 多个块分别回到循环头时，步进通常受条件控制，例如删除成功减长度，
            // 否则才增加索引。只抽取最后一个回边的 ++ 放进 for 头会使其每轮都执行。
            // 仅有唯一闩锁时，步进才可安全提升为 for 的 Increment。
            var latchBlocks = loop.Blocks
                .Where(block => block.To.Contains(loop.Header))
                .Distinct()
                .ToList();
            if (latchBlocks.Count != 1 || latchBlocks[0] != dw.Continue)
            {
                return false;
            }

            // Nested loops are prone to incorrect statement hoisting during for-conversion.
            // Keep them as while/do-while to preserve semantics first.
            var first = loop.Blocks.First();
            var idx = _context.Blocks.IndexOf(first);
            if (idx < 1)
            {
                return false;
            }

            //Get Initializer
            var prev = _context.Blocks[idx - 1];
            var externalPredecessors = loop.Header.From
                .Where(predecessor => !loop.Blocks.Contains(predecessor))
                .Distinct()
                .ToList();
            if (externalPredecessors.Count != 1 || externalPredecessors[0] != prev)
            {
                return false;
            }

            //Get Increment
            Expression step = null;
            Expression stepTarget = null;
            //the increment statement can be unary or binary
            var operationExp = dw.Continue.Statements
                .LastOrDefault(n => (n is IOperation));

            if (operationExp is UnaryExpression step1 && step1.Op.CanSelfAssign())
            {
                step = step1;
                stepTarget = step1.Target;
            }
            else if (operationExp is BinaryExpression step2 && step2.Op.CanSelfAssign())
            {
                step = step2;
                stepTarget = step2.Left;
            }
            else
            {
                return false;
            }

            // 循环前块可能还会缓存长度等值，计数器初始化并不一定是最后一条赋值。
            // 必须按闩锁步进的目标反查 initializer，否则会错过可安全提升的 for，
            // 也可能把无关赋值从原位置错误搬进循环头。
            var initializer = prev.Statements
                .OfType<BinaryExpression>()
                .LastOrDefault(assign => assign.Op == BinaryOp.Assign &&
                                         IsSameAssignmentTarget(assign.Left, stepTarget));
            if (initializer == null)
            {
                return false;
            }

            var l = initializer.Left;

            Expression forCondition = null;
            var headerCondition = first.Statements.LastOrDefault() as ConditionExpression;
            if (headerCondition != null)
            {
                // 嵌套循环的块地址不一定连续，Blocks.Last().End + 1 并非真实出口。
                // 入口分析已经解析出准确的 Break 块，应以它判断条件哪一侧离开循环。
                var exitStart = dw.Break?.Start ?? loop.Exit;
                if (headerCondition.TrueBranch == exitStart)
                {
                    forCondition = headerCondition.Condition.Invert();
                }
                else if (headerCondition.FalseBranch == exitStart)
                {
                    forCondition = headerCondition.Condition;
                }
            }

            // 只有初始化、条件和步进确实围绕同一目标时才提升为 for。
            // 否则前一块的无关赋值会被误当成 initializer，改变执行顺序。
            if (forCondition == null || !ReferencesAssignmentTarget(forCondition, l))
            {
                return false;
            }

            ((IOperation) step).IsSelfAssignment = true; //make increment to v4 += 2 instead of v4 + 2
            dw.Continue.Statements.Remove(step);

            //Get Condition
            dw.Condition = forCondition;
            first.Statements.Remove(headerCondition);

            dw.Continue.Statements.Remove(dw.Continue.Statements.LastOrDefault(stmt => stmt is IJump));

            f = new ForLogic {Initializer = initializer, Increment = step, Condition = dw.Condition, Body = dw.Body};
            prev.Statements.Remove(initializer);

            // for 的闩锁块可能在步进之前还承载本轮公共尾部。跳到这种块只是
            // 进入公共尾部，不能写成 continue（否则会直接跳过这些副作用）。
            // 提取步进和回边后仅当闩锁为空，才把它当作源码级 continue 目标。
            var syntaxContinue = dw.Continue.Statements.Count == 0
                ? dw.Continue
                : null;
            StructureLoopTransfers(loop, f.Body, syntaxContinue, dw.Break);

            return true;
        }

        private void StructureLoopTransfers(Loop loop, IEnumerable<Block> body, Block continueBlock, Block breakBlock)
        {
            var bodyBlocks = body?.ToList() ?? new List<Block>();
            // 父自然循环包含子循环的全部基本块。若父循环先把子循环的出口跳转
            // 改写成自己的 continue，子循环随后便无法识别该边其实是 break。
            // 转移语句只在当前循环独占的块中恢复；子循环会在自己的轮次处理。
            var childBlocks = loop.Children
                .SelectMany(child => child.Blocks)
                .ToHashSet();
            foreach (var bodyBlock in bodyBlocks)
            {
                if (childBlocks.Contains(bodyBlock))
                {
                    continue;
                }

                StructureBreakContinue(
                    bodyBlock, continueBlock, breakBlock);
            }

        }

        private static bool IsSameAssignmentTarget(Expression left, Expression right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is LocalExpression leftLocal && right is LocalExpression rightLocal)
            {
                return leftLocal.Slot == rightLocal.Slot;
            }

            if (left is IdentifierExpression leftIdentifier && right is IdentifierExpression rightIdentifier)
            {
                return string.Equals(leftIdentifier.FullName, rightIdentifier.FullName, System.StringComparison.Ordinal);
            }

            return false;
        }

        private static bool ReferencesAssignmentTarget(Expression expression, Expression target)
        {
            if (expression == null)
            {
                return false;
            }

            if (IsSameAssignmentTarget(expression, target))
            {
                return true;
            }

            switch (expression)
            {
                case ConditionExpression condition:
                    return ReferencesAssignmentTarget(condition.Condition, target);
                case BinaryExpression binary:
                    return ReferencesAssignmentTarget(binary.Left, target) ||
                           ReferencesAssignmentTarget(binary.Right, target);
                case UnaryExpression unary:
                    return ReferencesAssignmentTarget(unary.Target, target);
                case PropertyAccessExpression property:
                    return ReferencesAssignmentTarget(property.Instance, target) ||
                           ReferencesAssignmentTarget(property.Property, target);
                case IdentifierExpression identifier:
                    return ReferencesAssignmentTarget(identifier.Instance, target);
                case InvokeExpression invoke:
                    return ReferencesAssignmentTarget(invoke.Instance, target) ||
                           ReferencesAssignmentTarget(invoke.MethodExpression, target) ||
                           invoke.Parameters.Any(parameter => ReferencesAssignmentTarget(parameter, target));
                case PhiExpression phi:
                    return phi.PossibleExpressions.Any(possible => ReferencesAssignmentTarget(possible, target));
                default:
                    return false;
            }
        }

        internal void StructureBreakContinue(
            Block b, Block continueBlock, Block breakBlock)
        {
            if (b?.Statements == null)
            {
                return;
            }

            for (var i = 0; i < b.Statements.Count; i++)
            {
                var node = b.Statements[i];
                if (node is GotoExpression g)
                {
                    if (continueBlock != null && g.JumpTo == continueBlock.Start)
                    {
                        b.Statements[i] = new ContinueStatement();
                    }
                    else if (breakBlock != null && g.JumpTo == breakBlock.Start)
                    {
                        b.Statements[i] = new BreakStatement();
                    }
                }
            }
        }

        private static bool IsLoopBreakArm(Block block, Block loopBreak, Loop loop = null)
        {
            if (block == null || loopBreak == null)
            {
                return false;
            }

            if (block == loopBreak || block.Statements.Any(statement => statement is BreakStatement))
            {
                return true;
            }

            if (block.Statements.LastOrDefault() is not GotoExpression jump)
            {
                return false;
            }

            // 一个循环允许多条独立 break 边，它们不一定先汇合到 FindBreak 选中的
            // 公共出口。若纯跳转臂的目标不属于循环，仍应把它恢复成 break；否则
            // 该 goto 在收集阶段会消失，留下空 if/else，并可能把循环变成死循环。
            return jump.JumpTo == loopBreak.Start ||
                   loop != null && loop.Blocks.All(candidate => candidate.Start != jump.JumpTo);
        }

        private static Statement MoveLoopBreakArmToStatement(Block block, Block loopBreak)
        {
            if (block == null || block == loopBreak)
            {
                return new BreakStatement();
            }

            var result = new BlockStatement();
            var moved = block.Statements
                .Where(node => node is not GotoExpression && node is not BreakStatement)
                .ToList();
            foreach (var node in moved)
            {
                block.Statements.Remove(node);
                result.Statements.Add(node is Expression expression
                    ? new ExpressionStatement(expression)
                    : node);
            }
            result.Statements.Add(new BreakStatement());
            return result;
        }

        private static bool IsContinueArmWithPayload(Block block, Loop loop)
        {
            if (block == null || loop == null || !block.To.Contains(loop.Header))
            {
                return false;
            }

            var last = block.Statements.LastOrDefault();
            var endsInContinue = last is ContinueStatement ||
                                 last is GotoExpression jump &&
                                 jump.JumpTo == loop.Header.Start;
            return endsInContinue && block.Statements.Any(node =>
                node is not GotoExpression && node is not ContinueStatement);
        }

        private static Statement MoveLoopContinueArmToStatement(Block block)
        {
            var result = new BlockStatement();
            var moved = block.Statements
                .Where(node => node is not GotoExpression && node is not ContinueStatement)
                .ToList();
            foreach (var node in moved)
            {
                block.Statements.Remove(node);
                result.Statements.Add(node is Expression expression
                    ? new ExpressionStatement(expression)
                    : node);
            }

            result.Statements.Add(new ContinueStatement());
            return result;
        }

        private bool GetThenElseBlock(ConditionExpression condition, List<Block> blocks, out Block then,
            out Block @else)
        {
            if (blocks.Count < 2)
            {
                then = null;
                @else = null;
                return false;
            }

            Block toBlock = blocks.FirstOrDefault(b => b.Start == condition.JumpTo);
            Block elBlock = blocks.FirstOrDefault(b => b.Start == condition.ElseTo);
            if (toBlock != null && elBlock != null)
            {
                if (condition.JumpIf)
                {
                    then = toBlock;
                    @else = elBlock;
                }
                else
                {
                    then = elBlock;
                    @else = toBlock;
                }

                return true;
            }

            then = blocks[0];
            @else = blocks[1];
            return false;
        }

        private void MergeIfCondition(IfLogic logic, ConditionExpression condition)
        {
            if (condition == null || _context?.BlockTable == null ||
                !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueBlock) ||
                !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseBlock))
            {
                return;
            }

            //recursive to final condition and go back
            Expression exp = null;
            MergeIfCondition(condition, logic.ConditionBlock, logic.PostDominator, ref exp, ref trueBlock, ref falseBlock);
            if (exp != null)
            {
                // Unwrap any ConditionExpression wrapper to prevent self-referential cycles.
                // merge/exp may be a ConditionExpression from the base case; unwrap to its
                // inner condition to avoid setting condi.Condition = condi.
                while (exp is ConditionExpression unwrap)
                {
                    exp = unwrap.Condition;
                }

                if (logic.Condition is ConditionExpression condi)
                {
                    condi.Condition = exp;
                }
                else
                {
                    logic.Condition = exp;
                }

                logic.Then.Blocks = new List<Block> {trueBlock};
                logic.Else.Blocks = new List<Block> {falseBlock};

                // Remove standalone expression from the condition block that is now
                // part of the merged condition (e.g., a CALL result used as || operand).
                // Only the first operand (leftmost leaf of the || chain) can be in the
                // condition block as a standalone expression; others are in hidden blocks.
                if (logic.ConditionBlock != null)
                {
                    var mergedExpr = (logic.Condition is ConditionExpression ce) ? ce.Condition : exp;
                    var leftmost = mergedExpr;
                    while (leftmost is BinaryExpression bin &&
                           (bin.Op == BinaryOp.LogicOr || bin.Op == BinaryOp.LogicAnd))
                    {
                        leftmost = bin.Left;
                    }

                    var condIdx = logic.ConditionBlock.Statements.IndexOf(condition);
                    if (condIdx > 0)
                    {
                        var prev = logic.ConditionBlock.Statements[condIdx - 1];
                        if (prev is Expression prevExpr && ReferenceEquals(prevExpr, leftmost))
                        {
                            logic.ConditionBlock.Statements.RemoveAt(condIdx - 1);
                        }
                    }
                }
            }
        }

        private Block FindIfPostDominator(Block conditionBlock)
        {
            return _graph.FindImmediatePostDominator(conditionBlock);
        }

        /// <summary>
        /// 分支区域中的短路链也必须先从最早入口整体恢复。若直接从尾部逐个
        /// 物化普通 if，A &amp;&amp; call0() || B &amp;&amp; call1() 会被拆成嵌套
        /// if/else，共同继续块还可能被误收入最后一个 false 分支。
        /// </summary>
        private void StructureNestedConditionChains(
            IEnumerable<Block> candidates, ISet<Block> excluded = null)
        {
            foreach (var candidate in candidates
                         .Distinct()
                         .Where(block => !block.Hidden &&
                                         (excluded == null || !excluded.Contains(block)))
                         .OrderBy(block => block.Start)
                         .ToList())
            {
                // 前面的根条件可能已经把本块吸收到同一条短路链并隐藏。
                // 枚举快照仍包含它，若再次结构化会重复拼接条件并破坏共享分支。
                if (candidate.Hidden)
                {
                    continue;
                }

                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition != null &&
                    TryStructureConditionChain(
                        candidate, nestedCondition, out var nestedChain))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedChain.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 单臂条件的主体区域中可能还有“内层判断汇合后继续清理”的结构，
        /// 如 `if (A) { if (B) update(); cleanup(); }`。先物化这类具有不同
        /// 立即后支配点的内层判断，防止外围区域把 B 和 update/cleanup 顺序复制。
        /// 共享同一汇合点且有一条边直达汇合点的块仍属于 &&/|| 短路链，留给
        /// 后续链式恢复，避免把 `A || B` 错拆成反向嵌套判断。
        /// </summary>
        private void StructureStrictlyNestedDecisions(IEnumerable<Block> candidates)
        {
            var candidateSet = candidates.Distinct().ToHashSet();
            foreach (var outer in candidateSet
                         .Where(block => !block.Hidden && block.Statements.GetCondition() != null)
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                if (outer.Hidden)
                {
                    continue;
                }

                var continuation = outer.To.FirstOrDefault(candidate =>
                    outer.To.Any(other => other != candidate && CanReach(other, candidate)));
                if (continuation == null ||
                    (outer.From.Count == 1 &&
                     outer.From[0].Statements.GetCondition() != null &&
                     (FindIfPostDominator(outer.From[0]) == continuation ||
                      outer.From[0].To.Any(target => target == continuation))))
                {
                    continue;
                }

                var body = outer.To.First(block => block != continuation);
                Block nestedRoot = null;
                if (body.Hidden)
                {
                    // 条件 Phi 已把前半段谓词折叠进后继表达式时，原入口会成为
                    // 隐藏决策壳。只有两条路径确实归一到同一条件块才穿透它。
                    var passthrough = new HashSet<Block>();
                    var normalized = NormalizeCollapsedDecisionTarget(body, passthrough);
                    if (normalized != body && normalized.Statements.GetCondition() != null)
                    {
                        nestedRoot = normalized;
                    }
                }
                else if (body.Statements.GetCondition() != null)
                {
                    var innerContinuation = body.To.FirstOrDefault(candidate =>
                        body.To.Any(other => other != candidate && CanReach(other, candidate)));
                    innerContinuation ??= FindIfPostDominator(body);
                    var hasTwoPayloadArms = body.To.Count == 2 &&
                        body.To.All(target => target != continuation &&
                                              target.Dominator?[body.Id] == true &&
                                              HasBranchPayload(target));
                    if (innerContinuation != null &&
                        ((innerContinuation != continuation &&
                          innerContinuation.PostDominator != null &&
                          innerContinuation.PostDominator[continuation.Id]) ||
                         (innerContinuation == continuation && hasTwoPayloadArms)))
                    {
                        nestedRoot = body;
                    }
                }

                if (nestedRoot == null)
                {
                    continue;
                }

                // 提前 return 的布尔链应由专用谓词恢复整体处理；先拆其中一段会
                // 改变 default return 或公共继续路径。这里只抢救带普通副作用和
                // 清理尾部的嵌套守卫。
                if (CanReachReturnBefore(nestedRoot, continuation))
                {
                    continue;
                }

                var nestedCondition = nestedRoot.Statements.GetCondition();
                if (nestedCondition != null &&
                    StructureIfElse(nestedRoot, out var nestedLogic))
                {
                    nestedRoot.Statements.Replace(
                        nestedCondition, nestedLogic.Simplify().ToStatement());
                }

                var outerCondition = outer.Statements.GetCondition();
                if (outerCondition != null && StructureIfElse(outer, out var outerLogic))
                {
                    outer.Statements.Replace(
                        outerCondition, outerLogic.Simplify().ToStatement());
                }
            }
        }

        private bool CanReachReturnBefore(Block start, Block stop)
        {
            return _graph.CanReachMatchingBefore(
                start, stop,
                block => block.Statements.Any(statement =>
                    statement is ReturnExpression));
        }

        /// <summary>
        /// 判断从入口出发的每条路径是否都会在到达公共继续块前结束函数。
        /// 终止分支可能先执行若干副作用，再汇到单独的 return 块，不能只检查
        /// 入口块本身是否含 return；遇到环则保守返回 false。
        /// </summary>
        private bool AllPathsTerminateBefore(Block start, Block stop)
        {
            return _graph.AllPathsTerminateBefore(
                start, stop,
                block => block.Statements.Any(statement =>
                    statement is ReturnExpression or ThrowExpression));
        }

        /// <summary>
        /// 判断每条路径是否都会到达指定公共后继，或在到达前以 return/throw 结束。
        /// 图论上的后支配点会被提前返回推到函数出口，但源码结构仍可能有一个
        /// 正常流继续块；只有存在未终止且绕过继续块的路径时才应拒绝该候选。
        /// </summary>
        private bool AllPathsReachOrTerminateAt(Block start, Block stop, Loop loop = null)
        {
            return _graph.AllPathsReachOrTerminateAt(
                start, stop,
                block => block.Statements.Any(statement =>
                    statement is ReturnExpression or ThrowExpression),
                loop == null
                    ? null
                    : block => block == loop.Header || IsContinueTarget(block, loop));
        }

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

                var result = current.To.All(Visit);
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

            if (!AllPathsReachOrTerminateAt(continueEntry, sharedTarget, loop))
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

        /// <summary>
        /// 将一串只负责跳转的条件块一次性恢复为“到达目标体”的谓词。
        /// 例如 A 为真返回、否则检查 B，再检查 C，可直接得到 A || (B && C)，
        /// 避免逐块递归时重复改写共享条件树。
        /// </summary>
        private bool TryStructureConditionChain(
            Block root, ConditionExpression rootCondition, out IfLogic logic,
            bool multiWayOnly = false)
        {
            logic = null;
            if (_context.BlockTable == null)
            {
                return false;
            }

            if (TryStructureEqualitySwitchChain(root, out logic))
            {
                return true;
            }

            var chainPostDominator = FindIfPostDominator(root);
            var passthroughBlocks = new HashSet<Block>();
            var conditionBlocks = new HashSet<Block> { root };
            var terminals = new HashSet<Block>();
            var pending = new Stack<Block>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var condition = current.Statements.GetCondition();
                if (condition == null ||
                    !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                    !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
                {
                    return false;
                }

                trueTarget = NormalizeDecisionTarget(trueTarget, passthroughBlocks);
                falseTarget = NormalizeDecisionTarget(falseTarget, passthroughBlocks);

                foreach (var pair in new[]
                         {
                             (Target: trueTarget, Sibling: falseTarget),
                             (Target: falseTarget, Sibling: trueTarget)
                         })
                {
                    var target = pair.Target;
                    InlineShortCircuitAssignment(target);
                    // 根条件与后继条件直接共享同一个载荷块时，两者共同组成
                    // 短路谓词，例如 `x === void || x > min`。后继的另一条边
                    // 即使进入下一条顺序 guard，也不能因此把当前 OR 拆成
                    // `x !== void && x > min`。兄弟仍是条件块的形态则确实可能是
                    // 两个顺序守卫，继续使用下面的保守排除规则。
                    var sharesDirectPayloadTarget =
                        pair.Sibling.Statements.GetCondition() == null &&
                        HasBranchPayload(pair.Sibling) &&
                        target.To.Contains(pair.Sibling);
                    // 中间短路块必须只有条件本身；带准备语句的块是实际分支体。
                    // 此外它还必须能只经过条件块回到同级另一出口。这个约束会把
                    // 分支体开头的普通嵌套 if 排除在短路链之外。
                    if (target != root && target.Statements.IsCondition() &&
                        target.From.All(predecessor =>
                            predecessor.Statements.GetCondition() != null ||
                            predecessor.Statements.Count == 0 ||
                            IsDecisionPassthrough(predecessor)) &&
                        // 内层载荷落入兄弟条件通常表示两个顺序守卫，不能合并。
                        // 但两侧若不穿过彼此就能到达同一载荷，它们属于同一个
                        // 判定 DAG（典型为 `(A && B) || (C && D)`），仍须整体恢复。
                        // 兄弟块若同时是根的直接出口和后支配点，则属于公共继续流。
                        (!HasPayloadArmJoiningSibling(target) ||
                         sharesDirectPayloadTarget ||
                         SharePayloadArmWithoutCrossing(
                             target, pair.Sibling, chainPostDominator) ||
                         pair.Sibling == chainPostDominator &&
                         root.To.Contains(pair.Sibling)) &&
                        (CanReachThroughConditionBlocks(target, pair.Sibling) ||
                         sharesDirectPayloadTarget ||
                         SharePayloadArmWithoutCrossing(
                             target, pair.Sibling, chainPostDominator)))
                    {
                        // 菱形条件的共享节点可能先从一条边被暂记为终点，随后才
                        // 从另一条边证明它仍是条件链的一部分。升级为条件块时必须
                        // 撤销旧终点；否则同一块同时出现在 conditions/terminals，
                        // 二路短路表达式会被误判成三路分派并拆坏分支赋值。
                        terminals.Remove(target);
                        if (conditionBlocks.Add(target))
                        {
                            pending.Push(target);
                        }
                    }
                    else if (!conditionBlocks.Contains(target))
                    {
                        terminals.Add(target);
                    }
                }
            }

            // 单个条件不需要走链式恢复。
            if (conditionBlocks.Count <= 1 || terminals.Count < 2)
            {
                return false;
            }

            var terminalList = terminals.ToList();
            if (multiWayOnly && terminalList.Count <= 2)
            {
                return false;
            }

            if (terminalList.Count > 2)
            {
                var multiWay = TryStructureMultiWayConditionChain(
                    root, conditionBlocks, terminalList, passthroughBlocks, out logic);
                return multiWay;
            }

            Block bodyTarget;
            Block continuationTarget;
            Block branchPostDominator = null;
            var hasElseBranch = false;
            var bodyIsLoopBreak = false;
            var bodyIsLoopExitRegion = false;
            var continuationCompletesLoopIteration = false;
            var returnTargets = terminalList
                .Where(target => target.Statements.Any(statement => statement is ReturnExpression) ||
                                 FindIfPostDominator(target)?.Statements.Any(
                                     statement => statement is ReturnExpression) == true)
                .ToList();
            var pureReturnTargets = returnTargets
                .Where(target => target.Statements.All(statement =>
                    statement is ReturnExpression || statement is GotoExpression))
                .ToList();

            // 父循环的 Blocks 也包含所有子循环块。条件属于嵌套循环时必须选
            // 最内层循环，否则内层回边会被当成普通可达路径，两个出口看起来
            // 互相可达，完整短路链便无法恢复。
            var containingLoop = _context.LoopSet
                .Where(candidate => candidate.Contains(root))
                .OrderBy(candidate => candidate.Blocks.Count)
                .FirstOrDefault();
            var externalLoopTargets = containingLoop == null
                ? new List<Block>()
                : terminalList.Where(target => !containingLoop.Contains(target)).ToList();
            var internalLoopTargets = containingLoop == null
                ? new List<Block>()
                : terminalList.Where(containingLoop.Contains).ToList();

            // 循环内判断两个分支是否为“执行体 -> 公共尾部”时，只考察当前迭代。
            // 若沿回边进入下一轮，公共尾部当然也能再次到达执行体，会把实际单向
            // 关系误判成环并放弃整条短路条件链。
            var firstReachesSecond = containingLoop == null
                ? CanReach(terminalList[0], terminalList[1])
                : CanReachWithoutLoopBackEdge(terminalList[0], terminalList[1], containingLoop.Header);
            var secondReachesFirst = containingLoop == null
                ? CanReach(terminalList[1], terminalList[0])
                : CanReachWithoutLoopBackEdge(terminalList[1], terminalList[0], containingLoop.Header);
            if (firstReachesSecond && secondReachesFirst)
            {
                return false;
            }

            var loopContinueTargets = containingLoop == null
                ? new List<Block>()
                : terminalList.Where(target => target == containingLoop.Header ||
                                                IsContinueTarget(target, containingLoop))
                    .ToList();

            var containingLoopBreak = containingLoop?.Break ??
                                      (containingLoop?.LoopLogic as DoWhileLogic)?.Break ??
                                      (containingLoop == null ? null : FindBreak(containingLoop));

            // 条件链的一侧离开循环、另一侧回到闩锁时，外部目标是 break。
            // 普通可达性会沿下一轮循环最终到达退出块，因而不能用“可达”把它
            // 误判成公共尾部，否则 break 会退化成空 if。该分支也可能先执行
            // 多个基本块再退出循环，此时要把完整区域连同 break 一起收入正文。
            if (externalLoopTargets.Count == 1 && internalLoopTargets.Count == 1 &&
                (IsLoopBreakArm(externalLoopTargets[0], containingLoopBreak, containingLoop) ||
                 AllPathsReachLoopExitOrTerminate(
                     externalLoopTargets[0], containingLoopBreak, containingLoop)))
            {
                bodyTarget = externalLoopTargets[0];
                continuationTarget = internalLoopTargets[0];
                bodyIsLoopBreak = IsLoopBreakArm(
                    bodyTarget, containingLoopBreak, containingLoop);
                // 自然循环不会把终止函数的 return 块纳入 Blocks，因此它同样会
                // 出现在 externalLoopTargets 中，但语义不是“执行一段区域后 break”。
                // 直接 return 应保留为普通条件体；若按循环退出区域收集，找不到
                // 通往 break 的支配区域就会放弃整条短路谓词。
                bodyIsLoopExitRegion = !bodyIsLoopBreak &&
                                       !TerminalConditionChainPlanner
                                           .IsSimpleTerminalReturnBlock(bodyTarget);
            }
            // for 提升会把空闩锁归一化为循环头。此时循环头表示“条件未命中，
            // 继续下一轮”，绝不能被选成 if 主体，否则谓词会反转且真实副作用
            // 被提升为无条件语句。
            else if (loopContinueTargets.Count == 1 && terminalList.Count == 2)
            {
                continuationTarget = loopContinueTargets[0];
                bodyTarget = terminalList.First(target => target != continuationTarget);
                continuationCompletesLoopIteration = true;
            }
            // 一侧最终落入另一侧时，后者是公共尾部，即使它恰好只有 return
            // 也不能当成提前返回守卫，否则有副作用的可选分支会跳过公共返回。
            else if (firstReachesSecond || secondReachesFirst)
            {
                bodyTarget = firstReachesSecond ? terminalList[0] : terminalList[1];
                continuationTarget = firstReachesSecond ? terminalList[1] : terminalList[0];
            }
            else if (pureReturnTargets.Count == 1)
            {
                bodyTarget = pureReturnTargets[0];
                continuationTarget = terminalList.First(target => target != bodyTarget);
            }
            else
            {
                // 两个出口互不可达但共同汇合，是带 else 的短路条件。
                // 字节码通常先排列源码的 then 区域，因此用较早的入口作为正分支，
                // 再根据“到达该入口”的路径反推出完整的 && / || 谓词。
                branchPostDominator = FindIfPostDominator(root);
                if (branchPostDominator == null || terminals.Contains(branchPostDominator))
                {
                    return false;
                }

                bodyTarget = terminalList.OrderBy(target => target.Start).First();
                continuationTarget = terminalList.First(target => target != bodyTarget);
                hasElseBranch = true;
            }

            var memo = new Dictionary<Block, PathPredicate>();
            PathPredicate Build(Block current)
            {
                if (current == bodyTarget)
                {
                    return PathPredicate.True();
                }

                if (current == continuationTarget)
                {
                    return PathPredicate.False();
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
                var whenTrue = Build(NormalizeDecisionTarget(
                    _context.BlockTable[condition.TrueBranch], passthroughBlocks));
                var whenFalse = Build(NormalizeDecisionTarget(
                    _context.BlockTable[condition.FalseBranch], passthroughBlocks));
                if (whenTrue == null || whenFalse == null)
                {
                    return null;
                }

                var combined = PathPredicate.Combine(condition.Condition, whenTrue, whenFalse);
                memo[current] = combined;
                return combined;
            }

            var predicate = Build(root);
            if (predicate?.Expression == null || predicate.Constant != null)
            {
                return false;
            }

            IfLogic result;
            if (hasElseBranch)
            {
                var initialThen = CollectDominatedBranchRegion(
                    root, bodyTarget, branchPostDominator, true);
                var initialElse = CollectDominatedBranchRegion(
                    root, continuationTarget, branchPostDominator, true);
                if (initialThen.Count == 0 || initialElse.Count == 0)
                {
                    return false;
                }

                var nestedCandidates = initialThen.Concat(initialElse)
                    .Distinct()
                    .Where(candidate => !conditionBlocks.Contains(candidate))
                    .ToList();
                StructureNestedConditionChains(nestedCandidates, conditionBlocks);

                // 外层条件物化前先恢复两个分支区域里的内层条件，否则这些条件块
                // 随区域一起隐藏后只会留下裸比较表达式。
                foreach (var candidate in nestedCandidates
                             .OrderByDescending(candidate => candidate.Start))
                {
                    var nestedCondition = candidate.Statements.GetCondition();
                    if (nestedCondition != null && StructureIfElse(candidate, out var nestedLogic))
                    {
                        candidate.Statements.Replace(
                            nestedCondition, nestedLogic.Simplify().ToStatement());
                    }
                }

                var thenRegion = CollectDominatedBranchRegion(
                    root, bodyTarget, branchPostDominator, true);
                var elseRegion = CollectDominatedBranchRegion(
                    root, continuationTarget, branchPostDominator, true);
                if (thenRegion.Count == 0 || elseRegion.Count == 0)
                {
                    return false;
                }

                result = new IfLogic
                {
                    ConditionBlock = root,
                    Condition = predicate.Expression,
                    PostDominator = branchPostDominator,
                    Then = { Type = LogicalBlockType.BlockList, Blocks = thenRegion },
                    Else = { Type = LogicalBlockType.BlockList, Blocks = elseRegion }
                };
            }
            else
            {
                if (bodyIsLoopBreak)
                {
                    result = new IfLogic
                    {
                        ConditionBlock = root,
                        Condition = predicate.Expression,
                        PostDominator = continuationTarget,
                        Then =
                        {
                            Type = LogicalBlockType.Statement,
                            Statement = MoveLoopBreakArmToStatement(
                                bodyTarget, containingLoopBreak)
                        },
                        Else = { Type = LogicalBlockType.None }
                    };
                    if (bodyTarget != containingLoopBreak)
                    {
                        bodyTarget.Hidden = true;
                    }
                }
                else if (bodyIsLoopExitRegion)
                {
                    var initialBodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, containingLoopBreak, true);
                    StructureNestedConditionChains(initialBodyRegion, conditionBlocks);
                    foreach (var candidate in initialBodyRegion
                                 .Where(candidate => !conditionBlocks.Contains(candidate))
                                 .OrderByDescending(candidate => candidate.Start))
                    {
                        var nestedCondition = candidate.Statements.GetCondition();
                        if (nestedCondition != null &&
                            StructureIfElse(candidate, out var nestedLogic))
                        {
                            candidate.Statements.Replace(
                                nestedCondition, nestedLogic.Simplify().ToStatement());
                        }
                    }

                    var bodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, containingLoopBreak, true);
                    if (bodyRegion.Count == 0)
                    {
                        return false;
                    }

                    var body = new BlockStatement(bodyRegion, true);
                    body.Statements.Add(new BreakStatement());
                    bodyRegion.SafeHide();
                    result = new IfLogic
                    {
                        ConditionBlock = root,
                        Condition = predicate.Expression,
                        PostDominator = continuationTarget,
                        Then = { Type = LogicalBlockType.Statement, Statement = body },
                        Else = { Type = LogicalBlockType.None }
                    };
                }
                else
                {
                    var initialBodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, continuationTarget, true);
                    StructurePayloadLeafConditions(initialBodyRegion);
                    StructureNestedConditionChains(initialBodyRegion, conditionBlocks);
                    // 外层短路谓词只决定是否进入分支体；分支体内部仍可能包含
                    // 独立的 if/else。若直接收集基本块，这些条件会变成裸比较，
                    // 两侧副作用则被顺序输出。先从尾到头物化，再重新收集可见区域。
                    foreach (var candidate in initialBodyRegion
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

                    var bodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, continuationTarget, true);
                    if (bodyRegion.Count == 0)
                    {
                        bodyRegion = new List<Block> { bodyTarget };
                    }
                    result = new IfLogic
                    {
                        ConditionBlock = root,
                        Condition = predicate.Expression,
                        PostDominator = continuationTarget,
                        Then = { Type = LogicalBlockType.BlockList, Blocks = bodyRegion },
                        Else = { Type = LogicalBlockType.None }
                    };
                }
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

            logic = result;
            // 条件链的一端若直接回到循环头（或闩锁），这个目标表示“结束本轮”，
            // 并不是可供父级分支继续顺序执行的普通汇合点。把显式 continue 留在
            // 条件结构之后，可同时覆盖直接边和执行完正文后到达该目标的路径；
            // 即使条件恰位于循环体末尾，它也只是一条语义等价的冗余转移。
            if (continuationCompletesLoopIteration &&
                !HasOuterIterationCompletingOwner(
                    root, continuationTarget, containingLoop) &&
                !root.Statements.Any(statement => statement is ContinueStatement))
            {
                root.Statements.Add(new ContinueStatement());
            }
            return true;
        }

        /// <summary>
        /// 共享正文的布尔链中，一条条件路径可能不回到同级兄弟块，而是与兄弟块
        /// 分别直达同一个正文，例如 `(A &amp;&amp; B) || C` 的多个成功边。此时仍可
        /// 把该条件纳入同一谓词。但如果兄弟路径只能先经过当前块才到达后续正文，
        /// 当前块就是下一条 else-if（或分支体内嵌 if）的入口，不能仅因两者拥有
        /// 相同后支配点而把整棵分派树吞进第一条条件。
        /// </summary>
        private static bool SharePayloadArmWithoutCrossing(
            Block first, Block second, Block postDominator)
        {
            HashSet<Block> CollectPayloads(Block start, Block forbidden)
            {
                var payloads = new HashSet<Block>();
                var visited = new HashSet<Block>();
                var pending = new Stack<Block>();
                pending.Push(start);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (current == null || current == forbidden ||
                        current == postDominator || !visited.Add(current))
                    {
                        continue;
                    }

                    if (!current.Statements.IsCondition() &&
                        !IsDecisionPassthrough(current))
                    {
                        payloads.Add(current);
                        continue;
                    }

                    foreach (var target in current.To)
                    {
                        pending.Push(target);
                    }
                }

                return payloads;
            }

            var firstPayloads = CollectPayloads(first, second);
            return firstPayloads.Count > 0 &&
                   firstPayloads.Overlaps(CollectPayloads(second, first));
        }

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
            if (!_context.LoopSet.Any(loop => loop.Contains(root)))
            {
                normalContinuation = terminals
                    .Where(candidate => terminals.Any(other =>
                        other != candidate && CanReach(other, candidate)))
                    .Where(candidate => terminals.All(other =>
                        other == candidate || CanReach(other, candidate) ||
                        AllPathsTerminateBefore(other, candidate)))
                    .OrderBy(candidate => candidate.Start)
                    .FirstOrDefault();
                if (normalContinuation != null)
                {
                    postDominator = normalContinuation;
                }
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

                var trueTarget = NormalizeDecisionTarget(
                    _context.BlockTable[condition.TrueBranch], passthroughBlocks);
                var falseTarget = NormalizeDecisionTarget(
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
                var current = NormalizeDecisionTarget(pending.Pop(), passthroughBlocks);
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
                current = NormalizeDecisionTarget(current, passthroughBlocks);
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

        private static bool IsDecisionPassthrough(Block block)
        {
            return block != null && block.To.Count == 1 &&
                   block.Statements.All(statement => statement is GotoExpression);
        }

        private Block NormalizeDecisionTarget(
            Block block, HashSet<Block> passthroughBlocks)
        {
            var memo = new Dictionary<Block, Block>();
            var visiting = new HashSet<Block>();
            Block Normalize(Block current)
            {
                if (current == null || !visiting.Add(current))
                {
                    return current;
                }

                if (memo.TryGetValue(current, out var cached))
                {
                    visiting.Remove(current);
                    return cached;
                }

                Block result = current;
                if (IsDecisionPassthrough(current) &&
                    !_context.CachedEqualityConditionBlocks.Contains(current))
                {
                    passthroughBlocks?.Add(current);
                    result = Normalize(current.To[0]);
                }
                else if (current.Hidden && current.To.Count == 2 &&
                         (current.Statements.Count == 0 ||
                          current.Statements.IsCondition() &&
                          !EqualityDispatchAnalyzer.HasCachedSelector(current) &&
                          !_context.CachedEqualityConditionBlocks.Contains(current)))
                {
                    // `default: case X:` 会让该比较的真假边落到同一正文，但比较
                    // 仍承载 case X 的标签身份。普通跳板归一化不能因目标相同而
                    // 折叠具有缓存选择值定义身份的相等条件。
                    var normalizedFirst = Normalize(current.To[0]);
                    var normalizedSecond = Normalize(current.To[1]);
                    if (normalizedFirst == normalizedSecond)
                    {
                        passthroughBlocks?.Add(current);
                        result = normalizedFirst;
                    }
                }

                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Normalize(block);
        }

        /// <summary>
        /// 相等分派只保护与当前选择值来自同一次 VM 求值的比较块。
        /// </summary>
        /// <remarks>
        /// case 正文中也可能出现文本相同的相等比较，例如用选择变量构造三元值。
        /// 若仅凭“曾缓存过相等条件”就停止穿透，已经折叠进 Phi 的正文条件壳会
        /// 被误当成外层 case 标签，随后因 Hidden 过滤而丢失整个正文。
        /// </remarks>
        private Block NormalizeEqualityDispatchTarget(
            Block block, HashSet<Block> passthroughBlocks,
            Expression dispatchSelector)
        {
            var memo = new Dictionary<Block, Block>();
            var visiting = new HashSet<Block>();

            bool IsCurrentDispatchComparison(Block candidate)
            {
                var condition = candidate?.Statements.GetCondition();
                if (condition == null && candidate != null)
                {
                    _context.CachedEqualityConditions.TryGetValue(
                        candidate, out condition);
                }

                return condition?.Condition is BinaryExpression
                       {
                           Op: BinaryOp.Equal or BinaryOp.Congruent
                       } comparison &&
                       EqualityDispatchAnalyzer.AreSameSelectorEvaluation(
                           comparison.Left, dispatchSelector);
            }

            Block Normalize(Block current)
            {
                if (current == null || !visiting.Add(current))
                {
                    return current;
                }
                if (memo.TryGetValue(current, out var cached))
                {
                    visiting.Remove(current);
                    return cached;
                }

                Block result = current;
                if (IsDecisionPassthrough(current) &&
                    !IsCurrentDispatchComparison(current))
                {
                    passthroughBlocks?.Add(current);
                    result = Normalize(current.To[0]);
                }
                else if (current.Hidden && current.To.Count == 2 &&
                         (current.Statements.Count == 0 ||
                          current.Statements.IsCondition()) &&
                         !IsCurrentDispatchComparison(current))
                {
                    var normalizedFirst = Normalize(current.To[0]);
                    var normalizedSecond = Normalize(current.To[1]);
                    if (normalizedFirst == normalizedSecond)
                    {
                        passthroughBlocks?.Add(current);
                        result = normalizedFirst;
                    }
                }

                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Normalize(block);
        }

        private static bool CanReachThroughConditionBlocks(Block start, Block target)
        {
            var pending = new Stack<Block>();
            var visited = new HashSet<Block>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current == target)
                {
                    return true;
                }

                // ExpressionPass 会把短路右值合并进后继条件，留下“无 AST 语句但
                // 仍含求值指令”的单后继壳。路径分析必须与 NormalizeDecisionTarget
                // 使用同一跳板定义，否则不同阶段会对同一 CFG 边界得出相反结论。
                if (!current.Statements.IsCondition() &&
                    current.Statements.Count != 0 &&
                    !IsDecisionPassthrough(current))
                {
                    continue;
                }

                foreach (var next in current.To)
                {
                    if (next == target || next.Statements.IsCondition() ||
                        next.Statements.Count == 0 || IsDecisionPassthrough(next))
                    {
                        pending.Push(next);
                    }
                }
            }

            return false;
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
                groups.All(group => AllPathsReachOrTerminateAt(group.Target, defaultTarget));
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
                        leafTrue.To.Contains(leafFalse) && HasBranchPayload(leafTrue) ||
                        leafFalse.To.Contains(leafTrue) && HasBranchPayload(leafFalse);
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
                         candidate, defaultTarget, initialRegions,
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

        private bool CanReach(Block start, Block target)
        {
            return _graph.CanReach(start, target);
        }

        private bool CanReachWithoutLoopBackEdge(Block start, Block target, Block loopHeader)
        {
            // 仅屏蔽所属自然循环的回边；子循环的头块仍保留在图快照中。
            return _graph.CanReachWithoutEntering(start, target, loopHeader);
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

        private void StructureTerminalGuards(TerminalGuardStage stage)
        {
            foreach (var plan in TerminalGuardPlanner.Create(_context, stage))
            {
                var logic = TerminalGuardMaterializer.Materialize(plan);
                plan.Root.Statements.Replace(
                    plan.OriginalCondition, logic.Simplify().ToStatement());
            }
        }

        /// <summary>
        /// 恢复嵌套正文中的 `value = ...; if (test) return ...;`。从后向前
        /// 处理可保证外层分支收集到已经完成的早退守卫，而不是裸条件快照。
        /// </summary>
        private void StructureNestedPayloadReturnGuards()
        {
            foreach (var block in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             candidate.From.Count > 0 &&
                                             !candidate.Statements.IsCondition() &&
                                             candidate.Statements.GetCondition() != null &&
                                             candidate.To.Count == 2 &&
                                             candidate.To.Any(TerminalConditionChainPlanner
                                                 .IsSimpleTerminalReturnBlock) &&
                                             candidate.Statements
                                                 .Take(candidate.Statements.Count - 1)
                                                 .Any(statement =>
                                                     statement is BinaryExpression or UnaryExpression) &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                var condition = block.Statements.GetCondition();
                var allDirectPureReturnArms = block.To
                    .Where(target => target.Statements.Count > 0 &&
                                     target.Statements.Any(statement =>
                                         statement is ReturnExpression) &&
                                     target.Statements.All(statement =>
                                         statement is ReturnExpression or GotoExpression))
                    .ToList();
                var assignmentContinuesPredecessorGuard =
                    block.Statements.Take(block.Statements.Count - 1)
                        .OfType<BinaryExpression>()
                        .Any(assignment => assignment.Op == BinaryOp.Assign &&
                                           ReferencesAssignmentTarget(
                                               condition, assignment.Left)) &&
                    block.From.Any(predecessor =>
                        predecessor.Statements.GetCondition() != null &&
                        predecessor.To.Any(allDirectPureReturnArms.Contains));
                if (assignmentContinuesPredecessorGuard)
                {
                    continue;
                }

                if (TerminalGuardPlanner.TryAnalyze(block, out var terminalPlan) &&
                    terminalPlan.Kind == TerminalGuardKind.Return)
                {
                    var guard = TerminalGuardMaterializer.Materialize(terminalPlan);
                    block.Statements.Replace(
                        condition, guard.Simplify().ToStatement());
                    continue;
                }

                if (condition != null &&
                    TryStructurePureDecisionDagToReturnGuard(block, out var logic))
                {
                    block.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 先物化两臂都直接结束函数的叶子判断。外围类型/对象守卫若先收集区域，
        /// 这种内层 else-if 会留下裸比较，并把其中一个 return 提升成无条件返回。
        /// </summary>
        private void StructureTerminalReturnLeaves(IEnumerable<Block> candidates)
        {
            if (candidates == null || _context?.BlockTable == null)
            {
                return;
            }

            foreach (var candidate in candidates
                         .Where(block => !block.Hidden && block.To.Count == 2 &&
                                         block.Statements.GetCondition() != null)
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                bool IsPureReturn(Block target) =>
                    target.Statements.Count > 0 &&
                    target.Statements.All(statement =>
                        statement is ReturnExpression or GotoExpression) &&
                    target.Statements.Any(statement => statement is ReturnExpression);

                if (!candidate.To.All(IsPureReturn))
                {
                    continue;
                }

                if (EqualityDispatchAnalyzer.IsPartOfDispatchChain(candidate))
                {
                    continue;
                }

                var condition = candidate.Statements.GetCondition();

                // 两臂虽然都是 return，但其中一臂有时也是外围条件的公共出口。
                // 例如 `if (p == 0) return; return value;` 的最终 return 会同时被
                // 外围类型判断引用。这里只内联当前条件独占的提前返回，公共出口
                // 留在原控制流中，避免被错误复制进 if 后变成无条件 return。
                // 已合并成 &&/|| 的条件通常仍属于更大的判定 DAG，交给后续
                // 路径谓词合并；过早物化会把整体真假方向反转两次。
                var isMergedLogicalCondition = condition.Condition is BinaryExpression
                {
                    Op: BinaryOp.LogicAnd or BinaryOp.LogicOr
                };
                if (candidate.To.Any(target =>
                        EqualityDispatchAnalyzer.IsSharedCaseBody(candidate, target)))
                {
                    // 当前比较是相等分派的最后一个标签，且其中一臂还是前一
                    // 标签的公共正文。保留完整比较链，稍后由 equality 区域一次
                    // 取得所有标签与共享正文的所有权。
                    continue;
                }

                if (!isMergedLogicalCondition &&
                    TerminalGuardPlanner.TryAnalyze(candidate, out var terminalPlan) &&
                    terminalPlan.Kind == TerminalGuardKind.Return &&
                    terminalPlan.IsPureTerminal)
                {
                    var guard = TerminalGuardMaterializer.Materialize(terminalPlan);
                    candidate.Statements.Replace(
                        condition, guard.Simplify().ToStatement());
                    continue;
                }

                if (candidate.To.Any(target => target.From.Any(predecessor =>
                        predecessor != candidate &&
                        predecessor.Statements.GetCondition() != null)))
                {
                    continue;
                }

                if (StructureIfElse(candidate, out var logic))
                {
                    candidate.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// merge If condition
        /// </summary>
        /// <param name="condition">current condition</param>
        /// <param name="conditionBlock">current condition block</param>
        /// <param name="dominator">if dominator</param>
        /// <param name="merge">merged if condition expression</param>
        /// <param name="then">assumed if true block</param>
        /// <param name="else">actual else block</param>
        /// <param name="isOrChain">true when called from an || merge path (not the initial call)</param>
        /// <returns></returns>
        private bool MergeIfCondition(ConditionExpression condition, Block conditionBlock, Block dominator, ref Expression merge,
            ref Block then, ref Block @else, bool isOrChain = false)
        {
            if (condition == null || _context?.BlockTable == null ||
                !_context.BlockTable.ContainsKey(condition.TrueBranch) ||
                !_context.BlockTable.ContainsKey(condition.FalseBranch))
            {
                return false;
            }

            if (dominator != null && condition.TrueBranch == dominator.Start)
            {
                condition = (ConditionExpression) condition.Invert();
            }

            then = _context.BlockTable[condition.TrueBranch]; //TODO: check me later
            var trueBlock = _context.BlockTable[condition.TrueBranch];
            var falseBlock = _context.BlockTable[condition.FalseBranch];
            var trueIsContent = IsBranchContent(trueBlock);
            var falseIsContent = IsBranchContent(falseBlock);

            // Allow else if merging by checking if falseBlock contains another condition
            if (trueBlock != then && falseBlock != dominator && !falseBlock.Statements.IsCondition()) //it's else if, can not merge
            {
                @else = conditionBlock;
                return false;
            }

            if (!trueIsContent && dominator == falseBlock)
            {
                var trueCondition = trueBlock.Statements.GetCondition();
                var savedThen = then;
                var savedElse = @else;
                if (MergeIfCondition(trueCondition, trueBlock, dominator, ref merge, ref then, ref @else))
                {
                    trueBlock.Hidden = true;
                    merge = condition.Condition.And(merge);
                    return true;
                }
                then = savedThen;
                @else = savedElse;
            }

            // && 合并路径补充：当 dominator 不等于 falseBlock（如 try 块内部），
            // 但 true 块的条件的 false 分支指向同一个 falseBlock 时，
            // 仍可构成 && 短路模式。
            if (!trueIsContent && dominator != falseBlock)
            {
                var trueCondition = trueBlock.Statements.GetCondition();
                if (trueCondition != null &&
                    _context.BlockTable.TryGetValue(trueCondition.FalseBranch, out var trueFalseTarget) &&
                    trueFalseTarget == falseBlock)
                {
                    var savedThen = then;
                    var savedElse = @else;
                    // 使用共享的 falseBlock 作为递归的 dominator
                    if (MergeIfCondition(trueCondition, trueBlock, falseBlock, ref merge, ref then, ref @else))
                    {
                        trueBlock.Hidden = true;
                        merge = condition.Condition.And(merge);
                        return true;
                    }
                    then = savedThen;
                    @else = savedElse;
                }
            }

            // || merge path: check if falseBlock continues the || chain.
            // Use direct check: falseBlock has a condition whose TrueBranch matches
            // the current trueBlock — this works even when IsBranchContent considers
            // falseBlock as "content" due to non-condition predecessors.
            {
                var falseCondition2 = falseBlock.Statements.GetCondition();
                bool isOrCandidate = falseCondition2 != null && then == trueBlock &&
                                     _context.BlockTable.ContainsKey(falseCondition2.TrueBranch) &&
                                     _context.BlockTable[falseCondition2.TrueBranch] == trueBlock;

                if (isOrCandidate)
                {
                    if (falseCondition2 == null)
                    {
                        falseCondition2 = falseBlock.Statements.GetCondition();
                    }

                    if (falseCondition2 != null)
                    {
                        var savedThen = then;
                        var savedElse = @else;
                        if (MergeIfCondition(falseCondition2, falseBlock, dominator, ref merge, ref then, ref @else, isOrChain: true))
                        {
                            falseBlock.Hidden = true;
                            merge = condition.Condition.Or(merge);
                            return true;
                        }
                        then = savedThen;
                        @else = savedElse;
                    }
                }
            }

            // Fallback: this is the final condition in a || chain.
            // When we're inside a recursive || merge and the falseBlock doesn't continue
            // the chain (different TrueBranch or not a condition block), this condition
            // is the last in the || chain, and falseBlock is the "else" code.
            if (isOrChain && then == trueBlock)
            {
                @else = falseBlock;
                merge = condition;
                return true;
            }

            if (trueBlock == then && falseBlock == dominator) //final condition
            {
                @else = dominator;
                merge = condition;
                return true;
            }

            @else = falseBlock;
            return false;
        }

        private bool IsBranchContent(Block block)
        {
            // 非分支块（单出口）→ 内容块
            if (block.To.Count < 2) return true;

            // 多个前驱且全为纯条件块 → 内容（如 A & (B || C) 模式，难以合并）
            if (block.From.Count > 1 &&
                block.From.All(b => b.Statements.IsCondition()))
                return true;

            // 纯条件块（仅含一个 ConditionExpression）→ 条件链节点，不视为内容
            if (block.Statements.IsCondition())
                return false;

            // 有非条件内容（条件之前或之后有其他语句）→ 内容块，不应被合并进条件链
            return true;
        }

        /// <summary>
        /// 检查目标块是否是循环的 continue 路径（最终跳转回循环头部）
        /// </summary>
        private bool IsContinueTarget(Block target, Loop loop)
        {
            if (target == null) return false;

            // while/已提升的 for 往往没有独立闩锁块，continue 会直接跳到循环头。
            if (target == loop.Header)
                return true;

            // 直接是循环闩锁块（无语句，仅跳转回循环头部）
            if (target.Statements.Count == 0 && target.To.Count == 1 && target.To[0] == loop.Header)
                return true;

            // 无显式 for 闩锁的 while，源码 continue 常落在一个只含 JMP 的跳板块。
            // GotoExpression 仍保留时也要识别，否则会输出空 if 并把后续语句塞进 else。
            if (target.Statements.Count > 0 &&
                target.Statements.All(statement => statement is GotoExpression) &&
                target.To.Count == 1 && target.To[0] == loop.Header)
                return true;

            // 块只包含 ContinueStatement
            if (target.Statements.Count == 1 && target.Statements[0] is ContinueStatement)
                return true;

            // 空块且后继为 continue 目标（处理连续跳转链）
            if (target.Statements.Count == 0 && target.To.Count == 1)
            {
                var jumpTarget = target.To[0];
                if (jumpTarget != target && IsContinueTarget(jumpTarget, loop))
                    return true;
            }

            return false;
        }

        private void RemoveLastGoto(Block from, Block to)
        {
            // 调用者传入的是已经证明的结构化汇合点。只移除确实跳到该点的
            // 末尾跳转；忽略目标会把循环 break、continue 或外层区域跳转一起删掉。
            if (from?.Statements.LastOrDefault() is GotoExpression jump &&
                to != null && jump.JumpTo == to.Start)
            {
                from.Statements.RemoveAt(from.Statements.Count - 1);
            }
        }

        /// <summary>
        /// 将延续块的 "if (negated_cond) goto after_scope" 模式转换为正向条件的 then-only if。
        /// 编译器将 if (COND) { body } 编译为 "if (!COND) skip; body"，
        /// 导致 TrueBranch 指向 scope 外部（elseBlock）。
        /// 此方法反转条件，将 FalseBranch（实际 body）作为 then，无 else。
        /// </summary>
        private void StructureContinuationAsInvertedIf(
            Block block, ConditionExpression cond, Block outerElseBlock)
        {
            var invertedCond = (ConditionExpression)cond.Invert();
            var bodyBlock = _context.BlockTable[invertedCond.TrueBranch];

            var innerLogic = new IfLogic
            {
                ConditionBlock = block,
                Condition = invertedCond,
                PostDominator = outerElseBlock,
                Then = { Blocks = new List<Block> { bodyBlock } },
                Else = { Type = LogicalBlockType.None }
            };

            // 递归结构化 body 块
            if (bodyBlock.To.Count == 2 && bodyBlock.Statements.GetCondition() != null)
            {
                var bodyCondition = bodyBlock.Statements.GetCondition();
                if (StructureIfElse(bodyBlock, out IfLogic innerIf))
                {
                    bodyBlock.Statements.Replace(bodyCondition, innerIf.Simplify().ToStatement());
                }
            }

            // 在 body 分支内查找延续块
            if (bodyBlock.To.Any(b => b.Hidden))
            {
                var bodyVisited = new HashSet<int> { bodyBlock.Start };
                var bodyCurrentBlock = bodyBlock;
                while (true)
                {
                    var bodyCont = FindVisibleContinuation(
                        bodyCurrentBlock, bodyVisited, bodyBlock, outerElseBlock);
                    if (bodyCont == null) break;
                    innerLogic.Then.Blocks.Add(bodyCont);
                    bodyVisited.Add(bodyCont.Start);

                    if (bodyCont.To.Count == 2 && bodyCont.Statements.GetCondition() != null)
                    {
                        var bodyContinuationCondition = bodyCont.Statements.GetCondition();
                        if (StructureIfElse(bodyCont, out IfLogic nestedCont2))
                        {
                            bodyCont.Statements.Replace(bodyContinuationCondition,
                                nestedCont2.Simplify().ToStatement());
                        }
                    }

                    bodyCurrentBlock = bodyCont;
                }
            }

            block.Statements.Replace(cond, innerLogic.Simplify().ToStatement());
        }

        /// <summary>
        /// 通过 BFS 穿越隐藏块查找下一个可见延续块。
        /// 当 if/try 等结构化操作隐藏了中间块后，直接后继可能全部不可见，
        /// 需要沿着隐藏块的后继链传递，直到找到第一个可见的延续块。
        /// </summary>
        private Block FindVisibleContinuation(Block currentBlock, HashSet<int> visited, Block thenBlock, Block elseBlock)
        {
            var queue = new Queue<Block>();
            var seen = new HashSet<int>(visited);

            foreach (var t in currentBlock.To)
            {
                if (!seen.Contains(t.Start))
                    queue.Enqueue(t);
            }

            while (queue.Count > 0)
            {
                var b = queue.Dequeue();
                if (seen.Contains(b.Start))
                    continue;
                seen.Add(b.Start);

                // 不越过 else 块或超出范围
                if (b == elseBlock || b.Start >= elseBlock.Start || b.Start <= thenBlock.Start)
                    continue;

                if (!b.Hidden)
                    return b;

                // 隐藏块：继续沿其后继传递
                foreach (var next in b.To)
                {
                    if (!seen.Contains(next.Start))
                        queue.Enqueue(next);
                }
            }

            return null;
        }

        internal bool StructureIfElse(Block block, out IfLogic outIf)
        {
            outIf = null;
            if (block == null || !_structuringBlocks.Add(block.Id))
            {
                return false;
            }

            using var mutation = new ControlFlowMutationScope(_context.Blocks);
            try
            {
                var structured = StructureIfElseCore(block, out outIf, true);
                if (structured)
                {
                    mutation.Commit();
                }
                return structured;
            }
            finally
            {
                _structuringBlocks.Remove(block.Id);
            }
        }

        /// <summary>
        /// 在父区域冻结的所有权集合内尝试物化子条件。
        /// </summary>
        private bool TryStructureIfElseWithinOwnership(
            Block block, Block boundary, ISet<Block> ownedBlocks,
            out IfLogic outIf, out bool ownershipViolation)
        {
            outIf = null;
            ownershipViolation = false;

            bool Attempt(Block forcedBoundary, bool allowConditionChain,
                out IfLogic logic, out bool crossedBoundary)
            {
                logic = null;
                crossedBoundary = false;
                if (block == null || !_structuringBlocks.Add(block.Id))
                {
                    return false;
                }

                using var mutation = new ControlFlowMutationScope(_context.Blocks);
                try
                {
                    var structured = StructureIfElseCore(
                        block, out logic, allowConditionChain, forcedBoundary);
                    if (!structured)
                    {
                        return false;
                    }

                    crossedBoundary = mutation.HasChangesOutside(ownedBlocks);
                    if (crossedBoundary)
                    {
                        logic = null;
                        return false;
                    }

                    mutation.Commit();
                    return true;
                }
                finally
                {
                    _structuringBlocks.Remove(block.Id);
                }
            }

            var loop = _context.LoopSet
                .Where(candidate => candidate.Contains(block))
                .OrderBy(candidate => candidate.Blocks.Count)
                .FirstOrDefault();
            var boundaryReachCount = boundary == null
                ? 0
                : block.To.Count(target =>
                    _graph.CanReachWithoutEntering(
                        target, boundary, loop?.Header));
            var hasMixedBoundaryExit = block.To.Count == 2 &&
                                       boundaryReachCount == 1;

            // 一臂正常落入父公共尾部、另一臂结束本轮时，普通后支配点会跨到
            // 下一轮。此形态必须先使用父边界；若不能在所有权内完成，父计划
            // 整体回滚，不能退回会吞掉边界的普通结构化。
            if (hasMixedBoundaryExit)
            {
                if (Attempt(boundary, false, out outIf, out _))
                {
                    return true;
                }
                ownershipViolation = true;
                return false;
            }

            if (Attempt(null, true, out outIf, out var firstViolation))
            {
                return true;
            }

            ownershipViolation = firstViolation;
            return false;
        }

        private bool StructureIfElseCore(
            Block block, out IfLogic outIf, bool allowConditionChain,
            Block forcedPostDominator = null)
        {
            outIf = null;
            if (block.To.Count != 2)
            {
                return false;
            }

            var cond = (ConditionExpression) block.Statements.LastOrDefault(stmt => stmt is ConditionExpression);
            if (cond == null)
            {
                return false;
            }

            var loop = _context.LoopSet.FirstOrDefault(candidate => candidate.Contains(block));
            // 父自然循环的 Blocks 也包含全部子循环块。判断 continue 目标时必须
            // 使用最内层循环，否则跳到内层头部的分支会被当成普通 goto，随后
            // 退化成裸条件和无条件副作用。其余旧分支判定仍保留原循环选择，
            // 避免改变已稳定的外层循环区域划分。
            var transferLoop = _context.LoopSet
                .Where(candidate => candidate.Contains(block))
                .OrderBy(candidate => candidate.Blocks.Count)
                .FirstOrDefault();
            if (loop?.LoopLogic is IConditional conditionLogic)
            {
                if (conditionLogic.Condition == cond)
                {
                    return false;
                }
            }

            // 循环内一侧直接进入本轮公共尾部、另一侧可能 continue 时，应先按
            // 分支区域恢复。条件链算法以循环头为汇合点，会把公共尾部收进 else，
            // 使从嵌套 switch break 出来的路径跳过后续处理。
            var preferBranchRegion = false;
            if (transferLoop != null &&
                _context.BlockTable.TryGetValue(cond.TrueBranch, out var rawTrueTarget) &&
                _context.BlockTable.TryGetValue(cond.FalseBranch, out var rawFalseTarget))
            {
                bool ReachesNormalTail(Block start, Block tail)
                {
                    return tail != transferLoop.Header &&
                           !IsContinueTarget(tail, transferLoop) &&
                           tail.Statements.IsCondition() &&
                           CanReachWithoutLoopBackEdge(
                               start, tail, transferLoop.Header) &&
                           AllPathsReachOrTerminateAt(start, tail, transferLoop);
                }

                bool ContainsSwitchBreakToTail(Block start, Block tail)
                {
                    return _context.Blocks.Any(candidate =>
                    {
                        var nestedCondition = candidate.Statements.GetCondition();
                        if (candidate == block || candidate.Dominator == null ||
                            !candidate.Dominator[start.Id] ||
                            nestedCondition?.Condition is not BinaryExpression comparison ||
                            comparison.Op is not (BinaryOp.Equal or BinaryOp.Congruent) ||
                            !_context.BlockTable.TryGetValue(
                                nestedCondition.TrueBranch, out var nestedTrue) ||
                            !_context.BlockTable.TryGetValue(
                                nestedCondition.FalseBranch, out var nestedFalse) ||
                            tail.Dominator == null || tail.Dominator[candidate.Id])
                        {
                            return false;
                        }

                        var trueReachesTail = CanReachWithoutLoopBackEdge(
                            nestedTrue, tail, transferLoop.Header);
                        var falseReachesTail = CanReachWithoutLoopBackEdge(
                            nestedFalse, tail, transferLoop.Header);
                        if (trueReachesTail == falseReachesTail)
                        {
                            return false;
                        }

                        var other = trueReachesTail ? nestedFalse : nestedTrue;
                        return AllPathsReachOrTerminateAt(
                            other, tail, transferLoop);
                    });
                }

                var trueReachesFalse = ReachesNormalTail(
                    rawTrueTarget, rawFalseTarget);
                var falseReachesTrue = ReachesNormalTail(
                    rawFalseTarget, rawTrueTarget);
                preferBranchRegion = trueReachesFalse != falseReachesTrue &&
                    (trueReachesFalse
                        ? ContainsSwitchBreakToTail(rawTrueTarget, rawFalseTarget)
                        : ContainsSwitchBreakToTail(rawFalseTarget, rawTrueTarget));
            }

            if (allowConditionChain &&
                (TryStructureLoopSwitchBreakGuard(block, cond, out outIf) ||
                 (!preferBranchRegion &&
                  TryStructureConditionChain(block, cond, out outIf))))
            {
                return true;
            }

            var postDominator = forcedPostDominator ?? FindIfPostDominator(block);

            Block thenBlock = block.To.FirstOrDefault(b => b.Start == cond.TrueBranch);
            Block elseBlock = block.To.FirstOrDefault(b => b.Start == cond.FalseBranch);
            if (thenBlock == null || elseBlock == null)
            {
                thenBlock = block.To[0];
                elseBlock = block.To[1];
            }

            thenBlock = NormalizeConsumedConditionalPhiTarget(thenBlock);
            elseBlock = NormalizeConsumedConditionalPhiTarget(elseBlock);

            // 空的源码分支有时会被编译成一块只含 JMP 的跳板，而真正的公共
            // 继续点在跳板之后。区域分析若把跳板也算作分支正文，就会得到
            // “两臂各一个块”，从而无法识别另一臂其实是完整的单臂 if；内层
            // 判断已经物化后，外层条件最终只剩裸表达式。仅穿过当前条件独占、
            // 无载荷且精确到达后支配点的链，避免误吞共享入口。
            // 循环中的空跳板还可能编码 continue/break，必须保留给循环转移规则。
            if (transferLoop == null)
            {
                var thenContinuation = TryGetEmptyBranchPassthrough(
                    block, thenBlock, postDominator, out var thenPassthrough);
                var elseContinuation = TryGetEmptyBranchPassthrough(
                    block, elseBlock, postDominator, out var elsePassthrough);
                var thenIsEmpty = thenContinuation != thenBlock;
                var elseIsEmpty = elseContinuation != elseBlock;
                bool HasMaterializedNestedBody(Block candidate) =>
                    candidate?.Statements.Any(statement => statement is
                        IfStatement or SwitchStatement or TryStatement or
                        WhileStatement or DoWhileStatement or ForStatement) == true;
                var isPureTerminalContinuation = postDominator?.Statements.Count > 0 &&
                    postDominator.Statements.Any(statement => statement is ReturnExpression) &&
                    postDominator.Statements.All(statement =>
                        statement is ReturnExpression or GotoExpression);

                // 这里只修复“空臂跳到函数末尾、另一臂已经完整物化”的确切
                // 所有权冲突。普通单臂区域仍交给原分析器，避免空跳板的隐藏
                // 提前截断较长顺序正文。
                if (isPureTerminalContinuation && thenIsEmpty != elseIsEmpty &&
                    (thenIsEmpty
                        ? HasMaterializedNestedBody(elseBlock)
                        : HasMaterializedNestedBody(thenBlock)))
                {
                    if (thenIsEmpty)
                    {
                        thenPassthrough.SafeHide();
                        thenBlock = thenContinuation;
                    }
                    else
                    {
                        elsePassthrough.SafeHide();
                        elseBlock = elseContinuation;
                    }
                }
            }

            // 一条边直接进入公共继续块，另一条边经过多块处理后到达它（途中允许
            // 提前 return/throw）时，函数出口虽是图论后支配点，源码仍是单臂 if。
            // 先恢复这个正常继续点，避免把公共尾部吸进“直接到达”的那一支。
            IfRegionPlan regionPlan = null;
            if (forcedPostDominator == null && thenBlock != elseBlock)
            {
                bool Terminates(Block candidate) => candidate.Statements.Any(statement =>
                    statement is ReturnExpression or ThrowExpression);
                IfRegionExitKind ClassifyTerminal(Block candidate)
                {
                    if (candidate.Statements.Any(statement => statement is ThrowExpression))
                    {
                        return IfRegionExitKind.Throw;
                    }
                    if (candidate.Statements.Any(statement => statement is ReturnExpression))
                    {
                        return IfRegionExitKind.Return;
                    }
                    return IfRegionExitKind.Unknown;
                }
                Func<Block, bool> completesIteration = transferLoop == null
                    ? null
                    : candidate => candidate == transferLoop.Header ||
                                   IsContinueTarget(candidate, transferLoop);
                regionPlan = IfRegionAnalyzer.Analyze(
                    _graph,
                    block,
                    thenBlock,
                    elseBlock,
                    postDominator,
                    transferLoop?.Header,
                    Terminates,
                    completesIteration,
                    ClassifyTerminal);

                // 一侧真实落入另一侧时，后者就是源码的公共继续块。不能因为
                // 继续块自身含 return，就把“在此终止”误算成反向可达；否则
                // `if (C) { ... } shared(); return;` 会被写成带伪 else 的结构。
                postDominator = regionPlan.NormalContinuation;
            }

            if (TryMergeConsumedNestedBranch(
                    block, cond, thenBlock, elseBlock, postDominator, out outIf))
            {
                return true;
            }

            // Phi 已经消费掉的短路条件块会被标记为隐藏，但 CFG 边仍指向旧入口。
            // 外层 if 若继续把旧入口当作分支体，会重新物化一个空 if，甚至把
            // 真正的 return 只放进其中一侧。这里先穿过这些隐藏决策壳层。
            // 两侧都属于已折叠决策链时才同时归一化。只处理单侧会把默认参数
            // 初始化等“隐藏条件 + 实际赋值”形态错误改写成提前返回守卫。
            if (thenBlock.Hidden && elseBlock.Hidden)
            {
                thenBlock = NormalizeDecisionTarget(thenBlock, new HashSet<Block>());
                elseBlock = NormalizeDecisionTarget(elseBlock, new HashSet<Block>());
            }

            var logic = new IfLogic
            {
                ConditionBlock = block,
                Condition = cond,
                PostDominator = postDominator,
                Then = {Blocks = new List<Block> {thenBlock}},
                Else = {Blocks = new List<Block> {elseBlock}}
            };

            // 条件块先更新局部值，随后恰有一臂直接返回常量时，这是典型的
            // “更新后提前返回”守卫。必须早于条件合并处理，否则公共继续块会
            // 被反向收进 if，而常量 return 被提升成无条件执行。
            bool IsConstantReturnArm(Block target) =>
                target.Statements.Count > 0 &&
                target.Statements.All(statement =>
                    statement is ReturnExpression or GotoExpression) &&
                target.Statements.OfType<ReturnExpression>()
                    .Any(ret => ret.Return is ConstantExpression);
            var hasLeadingLocalUpdate = block.Statements.Any(statement =>
                statement is BinaryExpression binary &&
                (binary.IsSelfAssignment || binary.Op.CanSelfAssign()) &&
                binary.Left is LocalExpression);
            var thenIsConstantReturn = IsConstantReturnArm(thenBlock);
            var elseIsConstantReturn = IsConstantReturnArm(elseBlock);
            if (hasLeadingLocalUpdate && thenIsConstantReturn != elseIsConstantReturn)
            {
                logic.Condition = thenIsConstantReturn
                    ? cond
                    : cond.Invert();
                logic.Then.Type = LogicalBlockType.BlockList;
                logic.Then.Blocks = new List<Block>
                {
                    thenIsConstantReturn ? thenBlock : elseBlock
                };
                logic.Else.Type = LogicalBlockType.None;
                logic.Else.Blocks = new List<Block>();
                outIf = logic;
                return true;
            }

            // 只读计划已经证明某一臂为空且另一臂独占正文时，直接按计划生成
            // 单臂 if。这样普通分支不必再依赖“入口是否恰好直接连到另一入口”
            // 的编译布局；多基本块正文也由同一所有权集合一次收集。
            var plannedLoopContinueSingleArm = transferLoop != null &&
                postDominator == transferLoop.Header &&
                ((thenBlock == transferLoop.Header &&
                  regionPlan?.ThenBlocks.Count == 0 &&
                  regionPlan.ElseBlocks.Count > 0) ||
                 (elseBlock == transferLoop.Header &&
                  regionPlan?.ElseBlocks.Count == 0 &&
                  regionPlan.ThenBlocks.Count > 0));
            // 循环内一臂可以分成两种正常出口：部分路径落入另一臂入口，另一些
            // 路径 continue 结束本轮。这仍是单臂 if + 公共顺序尾部；若因存在
            // continue 就禁止区域计划，公共尾部会被错误写成 else。
            var plannedLoopMixedSingleArm = transferLoop != null &&
                postDominator != transferLoop.Header && regionPlan != null &&
                ((regionPlan.ThenReachesElseOrTerminates &&
                  regionPlan.ThenBlocks.Count > 0 &&
                  regionPlan.ElseBlocks.Count == 0) ||
                 (regionPlan.ElseReachesThenOrTerminates &&
                  regionPlan.ElseBlocks.Count > 0 &&
                  regionPlan.ThenBlocks.Count == 0));
            var plannedSingleArm = regionPlan != null &&
                (transferLoop == null || plannedLoopContinueSingleArm ||
                 plannedLoopMixedSingleArm) &&
                ((regionPlan.ThenBlocks.Count > 0 &&
                  regionPlan.ElseBlocks.Count == 0) ||
                 (regionPlan.ElseBlocks.Count > 0 &&
                  regionPlan.ThenBlocks.Count == 0));
            if (plannedSingleArm &&
                TryStructureDominatedBranchRegions(
                    block, thenBlock, elseBlock, postDominator, logic,
                    regionPlan))
            {
                // 单个条件也可能以循环头为“空分支”。若不显式结束本轮，父级
                // 分派会把它当成普通单臂 if，随后错误落入同层公共处理代码。
                // 更外层已有直接完成边时由外层统一拥有该转移，避免在内层重复。
                if (plannedLoopContinueSingleArm &&
                    !HasOuterIterationCompletingOwner(
                        block, postDominator, transferLoop) &&
                    !block.Statements.Any(statement => statement is ContinueStatement))
                {
                    block.Statements.Add(new ContinueStatement());
                }
                outIf = logic;
                return true;
            }

            // 单臂 if 的一侧会直接落入另一侧入口：C ? A : join，且 A -> join。
            // 循环中的后支配分析容易把 join 选成循环头，若先按“支配区域”收集，
            // 会把公共后继误塞进 else 并反转条件。入口含真实载荷时可直接按边
            // 关系恢复，不依赖循环上的后支配结果。分支入口本身仍可能是带准备
            // 语句的嵌套条件块，必须先物化内层 if；否则快速返回后只会留下裸条件。
            if (thenBlock.To.Contains(elseBlock) && HasBranchPayload(thenBlock))
            {
                StructureNestedBranchCondition(thenBlock);
                logic.Then.Type = LogicalBlockType.BlockList;
                logic.Then.Blocks = new List<Block> { thenBlock };
                logic.Else.Type = LogicalBlockType.None;
                RemoveLastGoto(thenBlock, elseBlock);
                outIf = logic;
                return true;
            }

            if (elseBlock.To.Contains(thenBlock) && HasBranchPayload(elseBlock))
            {
                StructureNestedBranchCondition(elseBlock);
                logic.Condition = cond.Invert();
                logic.Then.Type = LogicalBlockType.BlockList;
                logic.Then.Blocks = new List<Block> { elseBlock };
                logic.Else.Type = LogicalBlockType.None;
                RemoveLastGoto(elseBlock, thenBlock);
                outIf = logic;
                return true;
            }


            bool elseIsBreak = false;
            if (loop != null)
            {
                if (elseBlock.Start >= loop.Exit)
                {
                    elseIsBreak = true;
                }
            }

            // 循环体条件的一侧留在循环内、另一侧直接 return 时，外侧块属于
            // 条件体而不是循环后的顺序代码。例如 “if (x <= y) return i; i--;”。
            // 原实现因两个分支后继不同而放弃结构化，最终只输出了比较表达式。
            // 嵌套循环中的 break 必须相对最内层循环判断。父循环的 Blocks 会
            // 包含整个子循环，若使用父循环，两侧都会被误认为“仍在循环内”，
            // 退出边随后会被收集阶段丢掉，字符扫描一类循环甚至会变成死循环。
            var breakLoop = transferLoop ?? loop;
            if (breakLoop != null &&
                breakLoop.Contains(thenBlock) != breakLoop.Contains(elseBlock))
            {
                var externalBlock = breakLoop.Contains(thenBlock) ? elseBlock : thenBlock;
                var externalIsTrue = externalBlock == thenBlock;
                var loopBreak = breakLoop.Break ??
                                (breakLoop.LoopLogic as DoWhileLogic)?.Break ??
                                FindBreak(breakLoop);

                if (IsLoopBreakArm(externalBlock, loopBreak, breakLoop))
                {
                    logic.Condition = externalIsTrue ? cond : cond.Invert();
                    logic.Then.Type = LogicalBlockType.Statement;
                    logic.Then.Statement = MoveLoopBreakArmToStatement(externalBlock, loopBreak);
                    logic.Else.Type = LogicalBlockType.None;
                    outIf = logic;
                    return true;
                }

                if (AllPathsReachLoopExitOrTerminate(
                        externalBlock, loopBreak, breakLoop))
                {
                    var initialExitRegion = CollectDominatedBranchRegion(
                        block, externalBlock, loopBreak, true);
                    StructureNestedConditionChains(initialExitRegion);
                    foreach (var candidate in initialExitRegion
                                 .OrderByDescending(candidate => candidate.Start))
                    {
                        var nestedCondition = candidate.Statements.GetCondition();
                        if (nestedCondition != null &&
                            StructureIfElse(candidate, out var nestedLogic))
                        {
                            candidate.Statements.Replace(
                                nestedCondition, nestedLogic.Simplify().ToStatement());
                        }
                    }

                    var exitRegion = CollectDominatedBranchRegion(
                        block, externalBlock, loopBreak, true);
                    if (exitRegion.Count > 0)
                    {
                        var exitBody = new BlockStatement(exitRegion, true);
                        exitBody.Statements.Add(new BreakStatement());
                        exitRegion.SafeHide();
                        logic.Condition = externalIsTrue ? cond : cond.Invert();
                        logic.Then.Type = LogicalBlockType.Statement;
                        logic.Then.Statement = exitBody;
                        logic.Else.Type = LogicalBlockType.None;
                        outIf = logic;
                        return true;
                    }
                }

                if (externalBlock.Dominator != null &&
                    externalBlock.Dominator[block.Id] &&
                    externalBlock.Statements.Any(statement => statement is ReturnExpression))
                {
                    logic.Condition = externalIsTrue ? cond : cond.Invert();
                    logic.Then.Type = LogicalBlockType.BlockList;
                    logic.Then.Blocks = new List<Block> { externalBlock };
                    logic.Else.Type = LogicalBlockType.None;
                    outIf = logic;
                    return true;
                }
            }

            // continue 分支常与本轮最后一条副作用共处于同一基本块，例如
            // `if (step == 0) { timer.interval = 0; continue; }`。旧逻辑只识别
            // “纯 JMP”跳板，因此条件被留下为裸比较、赋值则变成无条件执行。
            // 有载荷的 continue 臂会终止当前路径，另一臂可继续按顺序结构化。
            if (transferLoop != null && !elseIsBreak)
            {
                // 另一臂也能到达该块时，它是本轮公共尾部，不是当前条件独占的
                // continue 载荷。把它移进某一臂会使赋值只在部分路径执行。
                var thenHasContinuePayload =
                    thenBlock != postDominator &&
                    IsContinueArmWithPayload(thenBlock, transferLoop) &&
                    !CanReachWithoutLoopBackEdge(
                        elseBlock, thenBlock, transferLoop.Header);
                var elseHasContinuePayload =
                    elseBlock != postDominator &&
                    IsContinueArmWithPayload(elseBlock, transferLoop) &&
                    !CanReachWithoutLoopBackEdge(
                        thenBlock, elseBlock, transferLoop.Header);
                if (thenHasContinuePayload != elseHasContinuePayload)
                {
                    var continueArm = thenHasContinuePayload ? thenBlock : elseBlock;
                    logic.Condition = thenHasContinuePayload ? cond : cond.Invert();
                    logic.Then.Type = LogicalBlockType.Statement;
                    logic.Then.Statement = MoveLoopContinueArmToStatement(continueArm);
                    logic.Else.Type = LogicalBlockType.None;
                    continueArm.Hidden = true;
                    outIf = logic;
                    return true;
                }
            }

            // 连续 continue 守卫合并：在循环体内，如果 if 的 true 分支是 continue
            // （跳转到循环闩锁块），则将连续的 continue 守卫合并为 || 条件
            if (transferLoop != null && !elseIsBreak && IsContinueTarget(thenBlock, transferLoop))
            {
                Expression mergedCondition = cond.Condition;
                Block currentElse = elseBlock;

                // 遍历后续块，将连续的 continue 守卫用 || 合并
                while (currentElse.Statements.IsCondition() && currentElse.To.Count == 2)
                {
                    var nextCond = currentElse.Statements.GetCondition();
                    var nextThenBlock = _context.BlockTable[nextCond.TrueBranch];
                    var nextElseBlock = _context.BlockTable[nextCond.FalseBranch];

                    if (IsContinueTarget(nextThenBlock, transferLoop))
                    {
                        mergedCondition = mergedCondition.Or(nextCond.Condition);
                        nextThenBlock.Hidden = true;
                        currentElse.Hidden = true;
                        currentElse = nextElseBlock;
                    }
                    else
                    {
                        break;
                    }
                }

                cond.Condition = mergedCondition;
                logic.Then.Type = LogicalBlockType.Statement;
                logic.Then.Statement = new ContinueStatement();
                thenBlock.Hidden = true;

                // 匹配失败 continue、匹配成功执行若干语句后 break 的搜索循环中，
                // 成功分支通常不属于自然循环块集合。若只输出 continue 守卫，
                // 成功正文会掉到循环外并在“完全未匹配”时也执行。将该退出区域
                // 显式收入 else，并在末尾补回 break。
                var exitPassthrough = new HashSet<Block>();
                var exitEntry = NormalizeDecisionTarget(currentElse, exitPassthrough);
                var loopExit = transferLoop.Break ??
                               (transferLoop.LoopLogic as DoWhileLogic)?.Break ??
                               FindBreak(transferLoop);
                if (IsLoopBreakArm(exitEntry, loopExit, transferLoop))
                {
                    logic.Else.Type = LogicalBlockType.Statement;
                    logic.Else.Statement = new BreakStatement();
                }
                else if (!transferLoop.Contains(exitEntry) &&
                         AllPathsReachLoopExitOrTerminate(
                             exitEntry, loopExit, transferLoop))
                {
                    var initialExitRegion = CollectDominatedBranchRegion(
                        block, exitEntry, loopExit, true);
                    StructureNestedConditionChains(initialExitRegion);
                    foreach (var candidate in initialExitRegion
                                 .OrderByDescending(candidate => candidate.Start))
                    {
                        var nestedCondition = candidate.Statements.GetCondition();
                        if (nestedCondition != null &&
                            StructureIfElse(candidate, out var nestedLogic))
                        {
                            candidate.Statements.Replace(
                                nestedCondition, nestedLogic.Simplify().ToStatement());
                        }
                    }

                    var exitRegion = CollectDominatedBranchRegion(
                        block, exitEntry, loopExit, true);
                    if (exitRegion.Count > 0)
                    {
                        var exitBody = new BlockStatement(exitRegion, true);
                        exitBody.Statements.Add(new BreakStatement());
                        exitRegion.SafeHide();
                        logic.Else.Type = LogicalBlockType.Statement;
                        logic.Else.Statement = exitBody;
                    }
                    else
                    {
                        logic.Else.Type = LogicalBlockType.None;
                    }
                }
                else
                {
                    logic.Else.Type = LogicalBlockType.None;
                }

                foreach (var passthrough in exitPassthrough)
                {
                    passthrough.Hidden = true;
                }

                outIf = logic;
                return true;
            }

            //if (thenBlock.To.Count == 2) //TODO: can be 2 - inner If
            // Also try merging when elseBlock has a condition sharing the same TrueBranch
            // as the current thenBlock — this indicates a potential || chain, even if
            // IsBranchContent considers elseBlock as "content" because the condition block
            // has extra statements (e.g., variable assignments before the condition).
            bool potentialOrChain = false;
            if (IsBranchContent(elseBlock))
            {
                var elseCond = elseBlock.Statements.GetCondition();
                if (elseCond != null)
                {
                    potentialOrChain = thenBlock.Start == elseCond.TrueBranch;
                }
            }

            // 当 thenBlock 等于后支配节点时，也需要尝试合并条件（即使两个分支
            // 都是 "content"）。这处理了 JF 跳过 body 块直达汇合点的 if-then 模式。
            var elseContinuesLoop = transferLoop != null &&
                                    IsContinueTarget(elseBlock, transferLoop);
            if (!elseContinuesLoop &&
                (!IsBranchContent(thenBlock) || (!IsBranchContent(elseBlock) && !elseIsBreak) || potentialOrChain
                 || (postDominator != null && thenBlock == postDominator)))
            {
                MergeIfCondition(logic, cond);
            }

            if (logic.Then.Blocks.Count > 0)
            {
                thenBlock = logic.Then.Blocks[0];
            }
            else
            {
                logic.Then.Blocks = new List<Block> {thenBlock};
            }

            if (logic.Else.Blocks.Count > 0)
            {
                elseBlock = logic.Else.Blocks[0];
            }

            // 两臂都结束函数、其中一臂只是较后的默认 return 时，源码通常是
            // `if (C) { payload; return x; } return y;`。若按普通双臂区域恢复，
            // 会生成多余 else，也更容易让外围条件把默认返回当作自己的分支。
            // 只在带载荷臂的字节码位置确实早于纯 return 时采用此布局。
            bool IsPureReturnOnly(Block candidate) =>
                candidate.Statements.Count > 0 &&
                candidate.Statements.All(statement =>
                    statement is ReturnExpression or GotoExpression) &&
                candidate.Statements.Any(statement => statement is ReturnExpression);
            bool HasTerminalReturn(Block candidate) =>
                candidate.Statements.Any(statement => statement is ReturnExpression);
            var thenIsPureReturnOnly = IsPureReturnOnly(thenBlock);
            var elseIsPureReturnOnly = IsPureReturnOnly(elseBlock);
            if (thenIsPureReturnOnly != elseIsPureReturnOnly)
            {
                var defaultReturn = thenIsPureReturnOnly ? thenBlock : elseBlock;
                var payloadReturn = thenIsPureReturnOnly ? elseBlock : thenBlock;
                if (HasTerminalReturn(payloadReturn) && payloadReturn.Start < defaultReturn.Start)
                {
                    logic.Condition = payloadReturn == thenBlock ? cond : cond.Invert();
                    logic.Then.Type = LogicalBlockType.BlockList;
                    logic.Then.Blocks = new List<Block> { payloadReturn };
                    logic.Else.Type = LogicalBlockType.None;
                    logic.Else.Blocks = new List<Block>();
                    RemoveLastGoto(payloadReturn, postDominator);
                    outIf = logic;
                    return true;
                }
            }

            if (TryStructureDominatedBranchRegions(
                    block, thenBlock, elseBlock, postDominator, logic,
                    regionPlan))
            {
                outIf = logic;
                return true;
            }

            // 两个分支根可能已分别物化成完整 IfStatement（共享 case/键分派常见）。
            // 此时基本块不再含 ConditionExpression，旧递归路径会误以为无法结构化，
            // 最终丢掉最外层条件并把两支顺序输出。若两支都明确汇合到同一后支配块，
            // 可直接把已物化语句作为 then/else 接回外层。
            if (postDominator != null && thenBlock != postDominator && elseBlock != postDominator &&
                HasBranchPayload(thenBlock) && HasBranchPayload(elseBlock) &&
                CanReach(thenBlock, postDominator) && CanReach(elseBlock, postDominator))
            {
                logic.Then.Type = LogicalBlockType.BlockList;
                logic.Then.Blocks = new List<Block> { thenBlock };
                logic.Else.Type = LogicalBlockType.BlockList;
                logic.Else.Blocks = new List<Block> { elseBlock };
                RemoveLastGoto(thenBlock, postDominator);
                RemoveLastGoto(elseBlock, postDominator);
                outIf = logic;
                return true;
            }

            // 若 thenBlock 本身是条件块（包含嵌套 if），递归结构化。
            // 当 MergeIfCondition 未处理（非 && / || 链）且 thenBlock 仍为条件块时，
            // 其内部条件需要被结构化为嵌套 IfStatement，否则会作为独立表达式输出。
            if (thenBlock.To.Count == 2 && thenBlock.Statements.GetCondition() != null)
            {
                var thenCondition = thenBlock.Statements.GetCondition();
                if (StructureIfElse(thenBlock, out IfLogic nestedThenIf))
                {
                    thenBlock.Statements.Replace(thenCondition, nestedThenIf.Simplify().ToStatement());
                }
            }

            // 当 then 块的后继中有被隐藏的块（如 try 体或已结构化的嵌套 if）时，
            // 迭代查找可见延续块，将它们全部加入 then 分支。
            // 如果延续块本身包含嵌套条件，同时进行递归结构化。
            // 通过 BFS 穿越隐藏块来查找可见延续块，处理多层嵌套结构。
            if (thenBlock.To.Any(b => b.Hidden))
            {
                var currentBlock = thenBlock;
                var visited = new HashSet<int> { thenBlock.Start };
                while (true)
                {
                    var visibleContinuation = FindVisibleContinuation(
                        currentBlock, visited, thenBlock, elseBlock);

                    if (visibleContinuation == null)
                        break;

                    logic.Then.Blocks.Add(visibleContinuation);
                    visited.Add(visibleContinuation.Start);

                    // 结构化新添加块中的嵌套条件
                    if (visibleContinuation.To.Count == 2 && visibleContinuation.Statements.GetCondition() != null)
                    {
                        var contCond = visibleContinuation.Statements.GetCondition();

                        // 检查延续块的 TrueBranch 是否指向外部 elseBlock（或超出范围）。
                        // 这表示编译器生成的 "if (negated_cond) goto after_scope" 模式，
                        // 需要反转条件并将实际 body 作为 then 分支，无 else。
                        if (contCond != null &&
                            contCond.TrueBranch >= elseBlock.Start)
                        {
                            StructureContinuationAsInvertedIf(
                                visibleContinuation, contCond, elseBlock);
                        }
                        else if (StructureIfElse(visibleContinuation, out IfLogic nestedContIf))
                        {
                            visibleContinuation.Statements.Replace(contCond,
                                nestedContIf.Simplify().ToStatement());
                        }
                    }

                    currentBlock = visibleContinuation;
                }
            }

            // 检查最后一个 then 块（可能是延续块）是否直接落入 elseBlock，
            // 以确定 elseBlock 是后续代码而非真正的 else 分支。
            var lastThenBlock = logic.Then.Blocks.Count > 0
                ? logic.Then.Blocks[logic.Then.Blocks.Count - 1]
                : thenBlock;

            if (thenBlock.To[0] == elseBlock)
            {
                logic.Else.Type = LogicalBlockType.None;
            }
            else if (logic.Then.Blocks.Count > 1
                     && logic.Then.Blocks.Any(b => b.To.Contains(elseBlock)))
            {
                // then 分支中存在延续块能直接落入 elseBlock，
                // 说明 elseBlock 是 if 之后的顺序代码而非 else 分支。
                logic.Else.Type = LogicalBlockType.None;
            }
            else if (postDominator != null && elseBlock == postDominator
                     && thenBlock.Statements.Any(s => s is not ConditionExpression && s is not GotoExpression))
            {
                // else 块就是后支配节点（所有路径的汇合点），
                // 不是真正的 else 分支而是公共后继代码。
                // 额外要求 then 块有实质性语句（排除仅含 ConditionExpression/GotoExpression 的块），
                // 避免将短路 && / || 表达式模式误识别为 if 语句。
                logic.Else.Type = LogicalBlockType.None;

                // 若 then 块内部有嵌套条件，递归结构化为 if 语句
                var nestedCond = thenBlock.Statements.GetCondition();
                if (nestedCond != null)
                {
                    if (StructureIfElse(thenBlock, out IfLogic nestedIf))
                    {
                        thenBlock.Statements.Replace(nestedCond, nestedIf.Simplify().ToStatement());
                    }
                }
            }
            else if (thenBlock.Statements.Any(s => s is ReturnExpression) &&
                     !(elseBlock.To.Count > 0 && elseBlock.To[0] == thenBlock))
            {
                // Then block returns from function and else block does NOT flow into
                // the then block. This means there's no fall-through; the else block is
                // just sequential code after the if, not an explicit else.
                // (When else flows into then, it's a short-circuit pattern like &&, not a simple if.)
                logic.Else.Type = LogicalBlockType.None;
            }
            else if (elseBlock.Statements.Any(s => s is ReturnExpression) &&
                     !(thenBlock.To.Count > 0 && thenBlock.To[0] == elseBlock))
            {
                // 对称的 guard-return：跳转分支 return，顺序分支继续执行。
                // 将条件反转后只输出 return 分支，继续块由外围支配区域顺序收集。
                logic.Condition = cond.Invert();
                logic.Then.Type = LogicalBlockType.BlockList;
                logic.Then.Blocks = new List<Block> { elseBlock };
                logic.Else.Type = LogicalBlockType.None;
            }
            else
            {
                if (elseIsBreak)
                {
                    logic.Else.Type = LogicalBlockType.Statement;
                    logic.Else.Statement = new BreakStatement();
                }
                else
                {
                    if (elseBlock.To.Count == 2) //can be inner if
                    {
                        if (StructureIfElse(elseBlock, out IfLogic innerIf))
                        {
                            logic.Else.Type = LogicalBlockType.Logical;
                            logic.Else.Logic = innerIf;
                            innerIf.ParentIf = logic;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else if (elseBlock.To.Count == 1)
                    {
                        // In a loop, both branches might jump back to loop header
                        if (loop != null && elseBlock.To[0] == loop.Header && thenBlock.To.Count > 0 && thenBlock.To[0] == loop.Header)
                        {
                            // Both branches jump back to loop header, this is valid
                            logic.Else.Type = LogicalBlockType.BlockList;
                            logic.Else.Blocks = new List<Block> {elseBlock};
                            RemoveLastGoto(elseBlock, elseBlock.To[0]);
                        }
                        else if (elseBlock.To[0] != thenBlock.To[0])
                        {
                            return false;
                        }
                        else
                        {
                            //hasElse = true;
                            logic.Else.Type = LogicalBlockType.BlockList;
                            logic.Else.Blocks = new List<Block> {elseBlock};
                            RemoveLastGoto(elseBlock, elseBlock.To[0]);
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            RemoveLastGoto(thenBlock, thenBlock.To[0]);

            outIf = logic;
            return true;
        }


        /// <summary>
        /// 判断分支入口是否含有真正会执行的语句。嵌套 if 先被物化后，入口块
        /// 往往是“赋值 + IfStatement”，另一侧仍是普通表达式；只检查 Statement
        /// 会漏掉这种合法的单块 if/else。纯 Condition/Goto 仍属于短路求值外壳，
        /// 不能据此构造分支，否则会把逻辑表达式误写成控制语句。
        /// </summary>
        private static bool HasBranchPayload(Block block)
        {
            return block?.Statements.Any(node =>
                node is not ConditionExpression && node is not GotoExpression) == true;
        }

        /// <summary>
        /// 将当前条件独占的空跳板链归一化为真实公共继续点。
        /// </summary>
        private static Block TryGetEmptyBranchPassthrough(
            Block owner, Block entry, Block continuation,
            out List<Block> passthrough)
        {
            passthrough = new List<Block>();
            if (owner == null || entry == null || continuation == null ||
                entry == continuation)
            {
                return entry;
            }

            var current = entry;
            var predecessor = owner;
            var visited = new HashSet<Block>();
            while (current != continuation && visited.Add(current))
            {
                if (current.Hidden || HasBranchPayload(current) ||
                    current.To.Count != 1 ||
                    current.From.Any(source => source != predecessor))
                {
                    passthrough.Clear();
                    return entry;
                }

                passthrough.Add(current);
                predecessor = current;
                current = current.To[0];
            }

            if (current != continuation)
            {
                passthrough.Clear();
                return entry;
            }

            return continuation;
        }

        /// <summary>
        /// 单臂分支入口可能同时包含准备语句和另一个条件，例如短路表达式
        /// <c>target &amp;&amp; (name = target.name) != ""</c> 的第二段。外层快速路径
        /// 只收集入口块，因此要先把入口后的受控区域折叠为嵌套 IfStatement。
        /// </summary>
        private void StructureNestedBranchCondition(Block branch)
        {
            var nestedCondition = branch?.Statements.GetCondition();
            if (nestedCondition != null && StructureIfElse(branch, out var nestedLogic))
            {
                branch.Statements.Replace(
                    nestedCondition, nestedLogic.Simplify().ToStatement());
            }
        }

        /// <summary>
        /// 内层 if 先物化时会隐藏自己的 then/else 数据块。外层条件若某一路正好
        /// 复用内层的一个分支，不能简单穿过隐藏块到公共尾部，否则该路语句会丢失。
        /// 例如 C ? (N ? A : B) : B 可安全合并为 (C &amp;&amp; N) ? A : B。
        /// </summary>
        private bool TryMergeConsumedNestedBranch(
            Block owner, ConditionExpression outerCondition,
            Block trueBlock, Block falseBlock, Block postDominator,
            out IfLogic logic)
        {
            logic = null;
            IfLogic mergedLogic = null;
            if (trueBlock == null || falseBlock == null ||
                (!trueBlock.Hidden && !falseBlock.Hidden))
            {
                return false;
            }

            var trueOwner = NormalizeDecisionTarget(trueBlock, new HashSet<Block>());
            var falseOwner = NormalizeDecisionTarget(falseBlock, new HashSet<Block>());

            bool TryBuild(Block nestedOwner, Block sharedBlock, bool nestedOnTrue)
            {
                var nested = nestedOwner?.Statements?.OfType<IfStatement>().LastOrDefault();
                if (nested == null)
                {
                    return false;
                }

                var nestedCondition = nested.Condition is ConditionExpression wrapper
                    ? wrapper.Condition
                    : nested.Condition;
                var outer = outerCondition.Condition;
                Expression combined;
                Statement thenStatement;
                Statement elseStatement;

                if (ContainsAnyOriginalNode(nested.Else, sharedBlock))
                {
                    // C ? (N ? A : B) : B  =>  (C && N) ? A : B
                    combined = nestedOnTrue
                        ? outer.And(nestedCondition)
                        : outer.Invert().And(nestedCondition);
                    thenStatement = nested.Then;
                    elseStatement = nested.Else;
                }
                else if (ContainsAnyOriginalNode(nested.Then, sharedBlock))
                {
                    // C ? (N ? A : B) : A  =>  (!C || N) ? A : B
                    combined = nestedOnTrue
                        ? outer.Invert().Or(nestedCondition)
                        : outer.Or(nestedCondition);
                    thenStatement = nested.Then;
                    elseStatement = nested.Else;
                }
                else
                {
                    return false;
                }

                mergedLogic = new IfLogic
                {
                    ConditionBlock = owner,
                    Condition = combined,
                    PostDominator = postDominator,
                    Then = { Type = LogicalBlockType.Statement, Statement = thenStatement },
                    Else = { Type = LogicalBlockType.Statement, Statement = elseStatement }
                };
                nestedOwner.Hidden = true;
                return true;
            }

            var merged = trueBlock.Hidden && TryBuild(trueOwner, falseBlock, true) ||
                         falseBlock.Hidden && TryBuild(falseOwner, trueBlock, false);
            logic = mergedLogic;
            return merged;
        }

        private static bool ContainsAnyOriginalNode(Statement statement, Block sourceBlock)
        {
            if (statement == null || sourceBlock?.Statements == null)
            {
                return false;
            }

            bool Contains(IAstNode node)
            {
                if (node == null)
                {
                    return false;
                }

                if (sourceBlock.Statements.Any(source =>
                        ReferenceEquals(source, node) ||
                        node is ExpressionStatement expressionStatement &&
                        ReferenceEquals(source, expressionStatement.Expression)))
                {
                    return true;
                }

                return node switch
                {
                    BlockStatement block => block.Statements.Any(Contains),
                    IfStatement nested => Contains(nested.Then) || Contains(nested.Else),
                    _ => false
                };
            }

            return Contains(statement);
        }
    }
}
