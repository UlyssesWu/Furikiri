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

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            _context = context;
            _context.LoopSetSort();

            IntervalAnalysisDoWhilePass();
            
            //try
            BuildTry();

            foreach (var b in _context.Blocks)
            {
                if (b.Hidden)
                {
                    continue;
                }

                if (StructureIfElse(b, out var logic))
                {
                    if (logic.Else.IsBreak)
                    {
                        //can be while!
                        var loop = context.LoopSet.FirstOrDefault(l => l.Header == b);
                        if (loop != null && loop.LoopLogic is DoWhileLogic dw)
                        {
                            dw.IsWhile = true;
                            dw.Condition = logic.Condition;
                            // Keep the original loop body. Only strip the conditional jump/condition
                            // from the header block if present, but do NOT remove the entire header,
                            // as it may contain legitimate loop body statements (e.g., inner if-else chains).
                            var lastCtrl = loop.Header.Statements.LastOrDefault(st =>
                                st is ConditionExpression || st is GotoExpression || st is BreakStatement || st is ContinueStatement);
                            if (lastCtrl != null)
                            {
                                loop.Header.Statements.Remove(lastCtrl);
                            }
                        }
                        else
                        {
                            b.Statements.Replace(logic.Condition, logic.Simplify().ToStatement());
                        }
                    }
                    else
                    {
                        b.Statements.Replace(logic.Condition, logic.Simplify().ToStatement());
                    }
                }
            }
            
            // Process if-else structures within loop bodies AFTER while detection
            foreach (var loop in context.LoopSet)
            {
                if (loop.LoopLogic is DoWhileLogic dw)
                {
                    StructureLoopBodyIfElse(dw.Body);
                }
                else if (loop.LoopLogic is ForLogic fl)
                {
                    StructureLoopBodyIfElse(fl.Body);
                }
            }

            return statement;
        }

        /// <summary>
        /// Structure if-else statements within loop body
        /// </summary>
        private void StructureLoopBodyIfElse(List<Block> bodyBlocks)
        {
            if (bodyBlocks == null) return;
            
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
                    
                    if (StructureIfElse(block, out var logic))
                    {
                        block.Statements.Replace(logic.Condition, logic.Simplify().ToStatement());
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
                    t.CatchBody = SelectBlocksInRange(catchOrExitTry.Start, tryEnd.Start);
                }

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
                if (lastBlock.Statements.GetCondition() is ConditionExpression lastCond &&
                    lastCond.TrueBranch == loop.Header.Start)
                {
                    dw.Condition = lastCond;
                    conditionBlock = lastBlock;
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
                conditionBlock?.Statements.Remove(conditionBlock.Statements.LastOrDefault(stmt => stmt is IJump));
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
                    foreach (var bodyBlock in dw.Body)
                    {
                        StructureBreakContinue(bodyBlock, dw.Continue, dw.Break);
                    }
                }

                loop.LoopLogic = logic;
            }
        }

        internal bool DoWhileToFor(Loop loop, DoWhileLogic dw, out ForLogic f)
        {
            f = null;

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
            var lastAssign =
                (BinaryExpression) prev.Statements.LastOrDefault(
                    n => n is BinaryExpression b && b.Op == BinaryOp.Assign);
            //if (lastAssign == null || !(lastAssign.Left is LocalExpression l))
            if (lastAssign == null)
            {
                return false;
            }

            var l = lastAssign.Left;

            //Get Increment
            Expression step = null;
            //the increment statement can be unary or binary
            var operationExp = dw.Continue.Statements
                .LastOrDefault(n => (n is IOperation));

            if (operationExp is UnaryExpression step1 && step1.Op.CanSelfAssign() &&
                (step1.Target == l || (step1.Target is LocalExpression le1 && l is LocalExpression le2 && le1.Slot == le2.Slot)))
            {
                step = step1;
            }
            else if (operationExp is BinaryExpression step2 && step2.Op.CanSelfAssign())
            {
                step = step2;
            }
            else
            {
                return false;
            }

            ((IOperation) step).IsSelfAssignment = true; //make increment to v4 += 2 instead of v4 + 2
            dw.Continue.Statements.Remove(step);

            //Get Condition
            if (first.Statements.LastOrDefault() is ConditionExpression condi)
            {
                if (condi.JumpTo == loop.Exit)
                {
                    dw.Condition = condi;
                    first.Statements.Remove(condi);
                }
            }

            dw.Continue.Statements.Remove(dw.Continue.Statements.LastOrDefault(stmt => stmt is IJump));

            f = new ForLogic {Initializer = lastAssign, Increment = step, Condition = dw.Condition, Body = dw.Body};
            prev.Statements.Remove(lastAssign);

            foreach (var bodyBlock in f.Body)
            {
                StructureBreakContinue(bodyBlock, dw.Continue, dw.Break);
            }

            return true;
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
            //recursive to final condition and go back
            var trueBlock = _context.BlockTable[condition.TrueBranch];
            var falseBlock = _context.BlockTable[condition.FalseBranch];
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
            BitArray d = new BitArray(conditionBlock.PostDominator);
            FindPostDominator(conditionBlock);

            return _context.Blocks.Find(b => b.Id == d.FirstIndexOf(true, conditionBlock.Id + 1));

            void FindPostDominator(Block condition)
            {
                if (!condition.Statements.IsCondition())
                {
                    return;
                }

                if (condition.Id == d.FirstIndexOf(true, conditionBlock.Id + 1))
                {
                    return;
                }

                d.And(condition.PostDominator);

                foreach (var block in condition.To)
                {
                    FindPostDominator(block);
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
            return block.To.Count < 2 ||
                   (block.From.Count > 1 &&
                    block.From.All(b => b.Statements.IsCondition())) || //it's hard to merge sth. like A & (B || C)
                   block.From.Any(b => !b.Statements.IsCondition());
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

        internal bool StructureIfElse(Block block, out IfLogic outIf)
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

            var loop = _context.LoopSet.FirstOrDefault(l => l.Contains(block));
            if (loop?.LoopLogic is IConditional conditionLogic)
            {
                if (conditionLogic.Condition == cond)
                {
                    return false;
                }
            }

            var postDominator = FindIfPostDominator(block);

            Block thenBlock = block.To.FirstOrDefault(b => b.Start == cond.TrueBranch);
            Block elseBlock = block.To.FirstOrDefault(b => b.Start == cond.FalseBranch);
            if (thenBlock == null || elseBlock == null)
            {
                thenBlock = block.To[0];
                elseBlock = block.To[1];
            }

            var logic = new IfLogic
            {
                ConditionBlock = block,
                Condition = cond,
                PostDominator = postDominator,
                Then = {Blocks = new List<Block> {thenBlock}},
                Else = {Blocks = new List<Block> {elseBlock}}
            };


            bool elseIsBreak = false;
            if (loop != null)
            {
                if (elseBlock.Start >= loop.Exit)
                {
                    elseIsBreak = true;
                }
            }

            // 连续 continue 守卫合并：在循环体内，如果 if 的 true 分支是 continue
            // （跳转到循环闩锁块），则将连续的 continue 守卫合并为 || 条件
            if (loop != null && !elseIsBreak && IsContinueTarget(thenBlock, loop))
            {
                Expression mergedCondition = cond.Condition;
                Block currentElse = elseBlock;

                // 遍历后续块，将连续的 continue 守卫用 || 合并
                while (currentElse.Statements.IsCondition() && currentElse.To.Count == 2)
                {
                    var nextCond = currentElse.Statements.GetCondition();
                    var nextThenBlock = _context.BlockTable[nextCond.TrueBranch];
                    var nextElseBlock = _context.BlockTable[nextCond.FalseBranch];

                    if (IsContinueTarget(nextThenBlock, loop))
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

            if (!IsBranchContent(thenBlock) || (!IsBranchContent(elseBlock) && !elseIsBreak) || potentialOrChain) //|| !IsBranchContent(elseBlock)
            {
                MergeIfCondition(logic, cond);
                //if (thenBlock.Statements.IsCondition())
                //{
                //}
                //else
                //{
                //    return false;
                //}
            }

            //if (thenBlock.To.Count != 1)
            //{
            //    return false;
            //}

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

            if (thenBlock.To[0] == elseBlock)
            {
                logic.Else.Type = LogicalBlockType.None;
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