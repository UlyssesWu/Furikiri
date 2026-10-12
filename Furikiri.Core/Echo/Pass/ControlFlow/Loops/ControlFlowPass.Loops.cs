using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
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
    }
}
