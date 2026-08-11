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
        private readonly HashSet<int> _structuringBlocks = new HashSet<int>();

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            _context = context;
            _context.LoopSetSort();

            NormalizeNoOpConditions();
            HideCollapsedPhiConditions();
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

            BuildTry();

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
        /// 条件跳转的真、假目标完全相同时，条件结果没有控制流用途。纯条件可直接
        /// 删除；若求值中含调用或赋值则只保留该表达式的副作用。否则这种字节码
        /// 会在嵌套块中泄露成 `typeof x == "String";` 一类伪语句。
        /// </summary>
        private void NormalizeNoOpConditions()
        {
            foreach (var block in _context.Blocks)
            {
                for (var index = block.Statements.Count - 1; index >= 0; index--)
                {
                    if (block.Statements[index] is not ConditionExpression condition ||
                        condition.TrueBranch != condition.FalseBranch)
                    {
                        continue;
                    }

                    if (HasObservableSideEffect(condition.Condition))
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

        private static bool HasObservableSideEffect(Expression expression)
        {
            return expression switch
            {
                null => false,
                InvokeExpression => true,
                ReturnExpression => true,
                ThrowExpression => true,
                BinaryExpression binary when binary.Op == BinaryOp.Assign ||
                                             binary.IsSelfAssignment ||
                                             binary.Op.CanSelfAssign() => true,
                BinaryExpression binary => HasObservableSideEffect(binary.Left) ||
                                           HasObservableSideEffect(binary.Right),
                UnaryExpression unary when unary.Op is UnaryOp.Inc or UnaryOp.Dec or
                    UnaryOp.Invalidate or UnaryOp.Eval => true,
                UnaryExpression unary => HasObservableSideEffect(unary.Target),
                ConditionExpression condition => HasObservableSideEffect(condition.Condition),
                PropertyAccessExpression property => HasObservableSideEffect(property.Instance) ||
                                                     HasObservableSideEffect(property.Property),
                _ => false
            };
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
                    if (condition == null || !block.Statements.IsCondition() ||
                        !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                        !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
                    {
                        continue;
                    }

                    var passthrough = new HashSet<Block>();
                    var normalizedTrue = NormalizeDecisionTarget(trueTarget, passthrough);
                    var normalizedFalse = NormalizeDecisionTarget(falseTarget, passthrough);
                    if (normalizedTrue != normalizedFalse || passthrough.Count == 0)
                    {
                        continue;
                    }

                    block.Hidden = true;
                    foreach (var passthroughBlock in passthrough)
                    {
                        passthroughBlock.Hidden = true;
                    }
                    changed = true;
                }
            }
        }

        /// <summary>
        /// Structure if-else statements within loop body
        /// </summary>
        private void StructureLoopBodyIfElse(List<Block> bodyBlocks)
        {
            if (bodyBlocks == null) return;

            var bodySet = bodyBlocks.ToHashSet();
            // 先从最早入口恢复最终 return 的整条短路链。入口本身未必直接连到
            // return（如 `(ext != "" && match) || (ext == "" && probe())`），
            // 只寻找“纯条件/跳板路径”可达循环外 return 的根，避免改写普通 if。
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

        private void BuildTry()
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
                ConditionExpression mergedTailPrefix = null;
                var headerCondition = loop.Header.Statements.GetCondition();
                var tailCondition = lastBlock.Statements.GetCondition();
                if (tailCondition is ConditionExpression lastCond &&
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
                        mergedTailPrefix = headerCondition;
                    }
                }
                else if (headerCondition != null && _context.BlockTable != null &&
                    _context.BlockTable.TryGetValue(headerCondition.TrueBranch, out var headerTrue) &&
                    _context.BlockTable.TryGetValue(headerCondition.FalseBranch, out var headerFalse) &&
                    loop.Contains(headerTrue) != loop.Contains(headerFalse))
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
                if (mergedTailPrefix != null)
                {
                    loop.Header.Statements.Remove(mergedTailPrefix);
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
                if (conditionBlock.Statements[i] is not BinaryExpression assignment ||
                    assignment.Op != BinaryOp.Assign ||
                    !ReferencesAssignmentTarget(condition, assignment.Left))
                {
                    continue;
                }

                if (assignment.IsDeclaration)
                {
                    var hasEarlierDeclaration = _context.Blocks.Any(block =>
                        block != conditionBlock && block.Start < conditionBlock.Start &&
                        conditionBlock.Dominator != null && conditionBlock.Dominator[block.Id] &&
                        block.Statements.OfType<BinaryExpression>().Any(previous =>
                            previous.IsDeclaration && previous.Op == BinaryOp.Assign &&
                            IsSameAssignmentTarget(previous.Left, assignment.Left)));
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

        /// <summary>
        /// 短路链中允许一种严格的带载荷条件块：一条赋值紧跟一条读取同一目标的
        /// 条件。将赋值内联后，`ui = source; ui === void` 可恢复为
        /// `(ui = source) === void`，该块也就能继续参与前后的 `||`/`&&` 合并。
        /// 只接受恰好两条语句，避免跨越其他副作用改变求值顺序。
        /// </summary>
        private void InlineShortCircuitAssignment(Block conditionBlock)
        {
            if (conditionBlock?.Statements.Count != 2 ||
                conditionBlock.Statements[0] is not BinaryExpression assignment ||
                assignment.Op != BinaryOp.Assign ||
                conditionBlock.Statements[1] is not ConditionExpression condition ||
                !ReferencesAssignmentTarget(condition, assignment.Left))
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

            StructureLoopTransfers(loop, f.Body, dw.Continue, dw.Break);

            return true;
        }

        private void StructureLoopTransfers(Loop loop, IEnumerable<Block> body, Block continueBlock, Block breakBlock)
        {
            var bodyBlocks = body?.ToList() ?? new List<Block>();
            foreach (var bodyBlock in bodyBlocks)
            {
                StructureBreakContinue(bodyBlock, continueBlock, breakBlock);
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

        internal void StructureBreakContinue(Block b, Block continueBlock, Block breakBlock)
        {
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

        private static bool IsLoopBreakArm(Block block, Block loopBreak)
        {
            if (block == null || loopBreak == null)
            {
                return false;
            }

            if (block == loopBreak || block.Statements.Any(statement => statement is BreakStatement))
            {
                return true;
            }

            return block.Statements.LastOrDefault() is GotoExpression jump &&
                   jump.JumpTo == loopBreak.Start;
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
            if (conditionBlock?.PostDominator == null)
            {
                return null;
            }

            var candidates = _context.Blocks
                .Where(block => block != conditionBlock && conditionBlock.PostDominator[block.Id])
                .ToList();

            // 立即后支配节点是严格后支配集合中离当前块最近的节点：
            // 若另一个候选节点还后支配它，则它不是“立即”节点。
            return candidates.FirstOrDefault(candidate =>
                !candidates.Any(other => other != candidate && other.PostDominator[candidate.Id]));
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
                    // 中间短路块必须只有条件本身；带准备语句的块是实际分支体。
                    // 此外它还必须能只经过条件块回到同级另一出口。这个约束会把
                    // 分支体开头的普通嵌套 if 排除在短路链之外。
                    if (target != root && target.Statements.IsCondition() &&
                        target.From.All(predecessor =>
                            predecessor.Statements.GetCondition() != null ||
                            IsDecisionPassthrough(predecessor)) &&
                        (CanReachThroughConditionBlocks(target, pair.Sibling) ||
                         FindIfPostDominator(target) == chainPostDominator))
                    {
                        if (conditionBlocks.Add(target))
                        {
                            pending.Push(target);
                        }
                    }
                    else
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
                return TryStructureMultiWayConditionChain(
                    root, conditionBlocks, terminalList, passthroughBlocks, out logic);
            }

            Block bodyTarget;
            Block continuationTarget;
            Block branchPostDominator = null;
            var hasElseBranch = false;
            var bodyIsLoopBreak = false;
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

            // for 提升会把空闩锁归一化为循环头。此时循环头表示“条件未命中，
            // 继续下一轮”，绝不能被选成 if 主体，否则谓词会反转且真实副作用
            // 被提升为无条件语句。
            if (loopContinueTargets.Count == 1 && terminalList.Count == 2)
            {
                continuationTarget = loopContinueTargets[0];
                bodyTarget = terminalList.First(target => target != continuationTarget);
            }
            // 条件链的一侧离开循环、另一侧回到闩锁时，外部目标是 break。
            // 普通可达性会沿下一轮循环最终到达退出块，因而不能用“可达”把它
            // 误判成公共尾部，否则 break 会退化成空 if。
            else if (externalLoopTargets.Count == 1 && internalLoopTargets.Count == 1 &&
                IsLoopBreakArm(externalLoopTargets[0], FindBreak(containingLoop)))
            {
                bodyTarget = externalLoopTargets[0];
                continuationTarget = internalLoopTargets[0];
                bodyIsLoopBreak = true;
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
                        Then = { Type = LogicalBlockType.Statement, Statement = new BreakStatement() },
                        Else = { Type = LogicalBlockType.None }
                    };
                }
                else
                {
                    var initialBodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, continuationTarget, true);
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

            foreach (var conditionBlock in conditionBlocks.Where(block => block != root))
            {
                conditionBlock.Hidden = true;
            }
            foreach (var passthroughBlock in passthroughBlocks)
            {
                passthroughBlock.Hidden = true;
            }

            logic = result;
            return true;
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

            var rootCondition = root.Statements.GetCondition();
            if (rootCondition != null &&
                (_context.BlockTable[rootCondition.TrueBranch] == postDominator ||
                 _context.BlockTable[rootCondition.FalseBranch] == postDominator))
            {
                // 根条件的一侧直接结束、另一侧才进入多路分派时，根是外围 gate。
                // 把它和内层 else-if 拉平会使后续分支在 gate=false 时也执行。
                return false;
            }

            var orderedTerminals = terminals.OrderBy(target => target.Start).ToList();
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

        private static Block NormalizeDecisionTarget(
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
                if (IsDecisionPassthrough(current))
                {
                    passthroughBlocks?.Add(current);
                    result = Normalize(current.To[0]);
                }
                else if (current.Hidden && current.Statements.IsCondition() && current.To.Count == 2)
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

                if (!current.Statements.IsCondition() && !IsDecisionPassthrough(current))
                {
                    continue;
                }

                foreach (var next in current.To)
                {
                    if (next == target || next.Statements.IsCondition() ||
                        IsDecisionPassthrough(next))
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
            var labels = new HashSet<Block>();
            var caseBlocks = new List<Block>();
            var groups = new List<(Block Target, List<Expression> Conditions, Block Owner)>();
            Expression comparisonTarget = null;
            var current = root;
            Block defaultTarget = null;

            while (current != null)
            {
                var condition = current.Statements.GetCondition();
                if (condition?.Condition is not BinaryExpression comparison ||
                    comparison.Op is not (BinaryOp.Equal or BinaryOp.Congruent))
                {
                    defaultTarget = current;
                    break;
                }

                if (comparisonTarget == null)
                {
                    comparisonTarget = comparison.Left;
                }
                else if (comparisonTarget?.ToString() != comparison.Left?.ToString())
                {
                    defaultTarget = current;
                    break;
                }

                if (!_context.BlockTable.TryGetValue(condition.TrueBranch, out var bodyTarget) ||
                    !_context.BlockTable.TryGetValue(condition.FalseBranch, out var nextTarget))
                {
                    return false;
                }

                bodyTarget = NormalizeDecisionTarget(bodyTarget, labels);
                nextTarget = NormalizeDecisionTarget(nextTarget, labels);
                caseBlocks.Add(current);

                var groupIndex = groups.FindIndex(group => group.Target == bodyTarget);
                if (groupIndex < 0)
                {
                    groups.Add((bodyTarget, new List<Expression> { comparison }, current));
                }
                else
                {
                    groups[groupIndex].Conditions.Add(comparison);
                }

                var nextCondition = nextTarget.Statements.GetCondition();
                if (nextTarget.Statements.IsCondition() &&
                    nextCondition?.Condition is BinaryExpression nextComparison &&
                    nextComparison.Op is BinaryOp.Equal or BinaryOp.Congruent &&
                    nextComparison.Left?.ToString() == comparisonTarget?.ToString())
                {
                    current = nextTarget;
                    continue;
                }

                defaultTarget = nextTarget;
                break;
            }

            if (caseBlocks.Count < 2 || groups.Count == 0 || defaultTarget == null)
            {
                return false;
            }

            var postDominator = FindIfPostDominator(root);
            if (postDominator == null)
            {
                return false;
            }

            // default 仍能只经过条件块回到既有 case 体时，这不是互斥的 switch，
            // 而是 `a == x || a == y || (P && Q)` 一类短路谓词。交给通用条件链
            // 一次性恢复，否则共享真分支会只挂在最后一项，前面的 case 变成空壳。
            if (defaultTarget != postDominator && groups.Any(group =>
                    CanReachThroughConditionBlocks(defaultTarget, group.Target)))
            {
                return false;
            }

            // 首个 case 为空且直接 break 时，外层空分支的 AST 隐藏顺序仍需由
            // 普通分支恢复处理；在这里接管会让后续非空 case 一并不可见。
            if (groups[0].Target == postDominator)
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
                if (defaultCondition != null && !defaultTarget.Statements.IsCondition())
                {
                    StructureNestedConditionChains(defaultNested);
                    // 带准备语句的 default 入口不是短路链根；内部较晚的 if 必须
                    // 先物化，否则入口整体 ToStatement 会把它们隐藏成裸比较。
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

            // 分支入口可能又是一条独立的相等比较链。必须在“从尾到头”处理
            // 区域内条件之前整体物化，否则尾部条件先被替换成 IfStatement 后，
            // 入口就只能看到一项比较，首个分支条件会退化成裸表达式。
            foreach (var candidate in groups.Select(group => group.Target).Distinct()
                         .Where(candidate => candidate != postDominator))
            {
                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition != null &&
                    TryStructureEqualitySwitchChain(candidate, out var nestedEquality))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedEquality.Simplify().ToStatement());
                }
            }

            // 先从 case 区域尾部恢复内层 if。case 入口只覆盖首个条件，后续的
            // 短路判断通常位于共享后继块；若直接物化入口，这些后继会以裸比较
            // 输出，真正受控的副作用语句则被隐藏掉。
            foreach (var candidate in initialRegions.Values.SelectMany(region => region)
                         .Distinct()
                         .Where(candidate => !groups.Any(group => group.Target == candidate))
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
                group => group.Target == postDominator
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

        private static bool CanReach(Block start, Block target)
        {
            var pending = new Queue<Block>();
            var visited = new HashSet<Block>();
            pending.Enqueue(start);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current == target)
                {
                    return true;
                }

                foreach (var next in current.To)
                {
                    pending.Enqueue(next);
                }
            }

            return false;
        }

        private static bool CanReachWithoutLoopBackEdge(Block start, Block target, Block loopHeader)
        {
            var pending = new Queue<Block>();
            var visited = new HashSet<Block>();
            pending.Enqueue(start);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current == target)
                {
                    return true;
                }

                foreach (var next in current.To)
                {
                    // 仅屏蔽所属自然循环的回边；子循环的头块必须保留，才能从
                    // 子循环执行体继续搜索到它的出口和当前迭代后继。
                    if (next != loopHeader)
                    {
                        pending.Enqueue(next);
                    }
                }
            }

            return false;
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
        /// 多基本块分支使用支配区域直接结构化。区域内条件从后向前处理，
        /// 使内层 if 先隐藏自己的子块，随后外层区域只收集仍可见的语句块。
        /// </summary>
        private bool TryStructureDominatedBranchRegions(
            Block conditionBlock, Block thenEntry, Block elseEntry,
            Block postDominator, IfLogic logic)
        {
            var initialThen = CollectDominatedBranchRegion(
                conditionBlock, thenEntry, postDominator, true);
            var initialElse = CollectDominatedBranchRegion(
                conditionBlock, elseEntry, postDominator, true);

            // 单块分支交给原有逻辑，它对 break/continue 和 else-if 有更细处理。
            if (initialThen.Count <= 1 && initialElse.Count <= 1)
            {
                return false;
            }

            var nestedCandidates = initialThen.Concat(initialElse)
                .Distinct()
                .OrderByDescending(candidate => candidate.Start);
            foreach (var candidate in nestedCandidates)
            {
                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition == null)
                {
                    continue;
                }

                if (StructureIfElse(candidate, out var nestedLogic))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedLogic.Simplify().ToStatement());
                }
            }

            var thenRegion = CollectDominatedBranchRegion(
                conditionBlock, thenEntry, postDominator, true);
            var elseRegion = CollectDominatedBranchRegion(
                conditionBlock, elseEntry, postDominator, true);

            logic.Then.Type = thenRegion.Count == 0
                ? LogicalBlockType.None
                : LogicalBlockType.BlockList;
            logic.Then.Blocks = thenRegion;
            logic.Else.Type = elseRegion.Count == 0
                ? LogicalBlockType.None
                : LogicalBlockType.BlockList;
            logic.Else.Blocks = elseRegion;
            return thenRegion.Count > 0 || elseRegion.Count > 0;
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
            var gt = from.Statements.LastOrDefault(st =>
                st is GotoExpression);
            if (gt != null)
            {
                from.Statements.Remove(gt);
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

            try
            {
                return StructureIfElseCore(block, out outIf, true);
            }
            finally
            {
                _structuringBlocks.Remove(block.Id);
            }
        }

        private bool StructureIfElseCore(Block block, out IfLogic outIf, bool allowConditionChain)
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

            if (allowConditionChain && TryStructureConditionChain(block, cond, out outIf))
            {
                return true;
            }

            var postDominator = FindIfPostDominator(block);

            Block thenBlock = block.To.FirstOrDefault(b => b.Start == cond.TrueBranch);
            Block elseBlock = block.To.FirstOrDefault(b => b.Start == cond.FalseBranch);
            if (thenBlock == null || elseBlock == null)
            {
                thenBlock = block.To[0];
                elseBlock = block.To[1];
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
            if (loop != null && loop.Contains(thenBlock) != loop.Contains(elseBlock))
            {
                var externalBlock = loop.Contains(thenBlock) ? elseBlock : thenBlock;
                var externalIsTrue = externalBlock == thenBlock;
                var loopBreak = (loop.LoopLogic as DoWhileLogic)?.Break ?? FindBreak(loop);

                if (IsLoopBreakArm(externalBlock, loopBreak))
                {
                    logic.Condition = externalIsTrue ? cond : cond.Invert();
                    logic.Then.Type = LogicalBlockType.Statement;
                    logic.Then.Statement = MoveLoopBreakArmToStatement(externalBlock, loopBreak);
                    logic.Else.Type = LogicalBlockType.None;
                    outIf = logic;
                    return true;
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
                var thenHasContinuePayload = IsContinueArmWithPayload(thenBlock, transferLoop);
                var elseHasContinuePayload = IsContinueArmWithPayload(elseBlock, transferLoop);
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
                logic.Else.Type = LogicalBlockType.None;
                thenBlock.Hidden = true;

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
            if (!IsBranchContent(thenBlock) || (!IsBranchContent(elseBlock) && !elseIsBreak) || potentialOrChain
                || (postDominator != null && thenBlock == postDominator))
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

            if (TryStructureDominatedBranchRegions(
                    block, thenBlock, elseBlock, postDominator, logic))
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
