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

            if (transferLoop == null && IsSharedVoidContinuation(forcedPostDominator) &&
                (thenBlock == forcedPostDominator || elseBlock == forcedPostDominator))
            {
                var bodyEntry = thenBlock == forcedPostDominator ? elseBlock : thenBlock;
                var bodyRegion = CollectDominatedBranchRegion(
                    block, bodyEntry, forcedPostDominator, true);
                if (bodyRegion.Count > 0 &&
                    AllPathsReachOrTerminateAt(bodyEntry, forcedPostDominator))
                {
                    // Freeze the empty switch arm before recovering the body.
                    // Its shared return must remain visible to later cases.
                    StructureNestedConditionChains(bodyRegion);
                    foreach (var nested in bodyRegion.OrderByDescending(candidate => candidate.Start))
                    {
                        var nestedCondition = nested.Hidden ? null : nested.Statements.GetCondition();
                        if (nestedCondition != null && StructureIfElse(nested, out var nestedLogic))
                            nested.Statements.Replace(nestedCondition, nestedLogic.Simplify().ToStatement());
                    }
                    outIf = new IfLogic
                    {
                        ConditionBlock = block,
                        Condition = thenBlock == forcedPostDominator
                            ? cond.Condition.Invert() : cond.Condition,
                        PostDominator = forcedPostDominator,
                        Then = { Type = LogicalBlockType.BlockList,
                            Blocks = bodyRegion.Where(candidate => !candidate.Hidden).ToList() },
                        Else = { Type = LogicalBlockType.None }
                    };
                    return true;
                }
            }

            if (transferLoop == null &&
                TerminalGuardPlanner.TryAnalyze(block, out var sharedTailGuard) &&
                sharedTailGuard.IsPureTerminal &&
                sharedTailGuard.Continuation.Dominator?[block.Id] != true)
            {
                // A return in the shared successor does not make that successor
                // an exclusive arm. Keep its preceding effects on all normal paths.
                outIf = TerminalGuardMaterializer.Materialize(sharedTailGuard);
                return true;
            }

            if (transferLoop != null)
            {
                Block NormalizeNormalArm(Block entry)
                {
                    if (!IsDecisionPassthrough(entry)) return entry;
                    var passthrough = new HashSet<Block>();
                    var target = NormalizeDecisionTarget(entry, passthrough);
                    if (!transferLoop.Contains(target) || IsContinueTarget(target, transferLoop))
                        return entry;
                    foreach (var candidate in passthrough) candidate.Hidden = true;
                    return target;
                }
                thenBlock = NormalizeNormalArm(thenBlock);
                elseBlock = NormalizeNormalArm(elseBlock);
                var thenContinues = IsContinueTarget(thenBlock, transferLoop) ||
                    IsContinueArmWithPayload(thenBlock, transferLoop);
                var elseContinues = IsContinueTarget(elseBlock, transferLoop) ||
                    IsContinueArmWithPayload(elseBlock, transferLoop);
                if (thenContinues != elseContinues)
                {
                    var normalTarget = thenContinues ? elseBlock : thenBlock;
                    if (transferLoop.Contains(normalTarget) &&
                        normalTarget.Dominator?[block.Id] != true)
                    {
                        // The normal arm is entered by other guards as well.
                        // Own only the continue; the shared decision/body must
                        // remain available to every incoming path.
                        var transfer = thenContinues ? thenBlock : elseBlock;
                        var transferStatement = IsContinueArmWithPayload(transfer, transferLoop)
                            ? MoveLoopContinueArmToStatement(transfer)
                            : (Statement)new ContinueStatement();
                        outIf = new IfLogic
                        {
                            ConditionBlock = block,
                            Condition = thenContinues ? cond : cond.Invert(),
                            PostDominator = normalTarget,
                            Then = { Type = LogicalBlockType.Statement, Statement = transferStatement },
                            Else = { Type = LogicalBlockType.None }
                        };
                        if (transfer != transferLoop.Header) transfer.Hidden = true;
                        return true;
                    }
                }
            }

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
                // 空臂精确到达后支配点，另一臂已经完整物化时，公共尾部
                // 可以包含普通调用；不能只接受纯 return，否则会漏掉外层守卫。
                // 普通未物化区域仍交给原分析器，避免截断较长顺序正文。
                if (thenIsEmpty != elseIsEmpty &&
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
    }
}
