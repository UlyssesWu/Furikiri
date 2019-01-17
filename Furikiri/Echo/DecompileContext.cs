using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Furikiri.Echo.Patterns;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    internal class DecompileContext
    {
        public Dictionary<TjsVarType, int> ParamCounts = new Dictionary<TjsVarType, int>()
        {
            {TjsVarType.Int, 0},
            {TjsVarType.Real, 0},
            {TjsVarType.String, 0},
            {TjsVarType.Octet, 0},
            {TjsVarType.Object, 0},
            {TjsVarType.Void, 0},
            {TjsVarType.Null, 0},
        };

        internal Dictionary<int, HashSet<BranchType>> Branches { get; set; } =
            new Dictionary<int, HashSet<BranchType>>();

        internal Block EntryBlock { get; set; }

        /// <summary>
        /// A fake block for any return
        /// </summary>
        internal Block ExitBlock { get; set; } = new Block(-1);

        internal List<Block> Blocks { get; set; } = new List<Block>();
        internal List<Loop> LoopSet { get; set; } = new List<Loop>();

        public CodeObject Object { get; set; }
        public List<DetectHandler> Detectors { get; set; }
        public List<Instruction> InstructionQueue { get; set; } = new List<Instruction>();
        public List<IPattern> Patterns { get; set; } = new List<IPattern>();
        public Dictionary<int, ITjsVariant> Vars { get; set; } = new Dictionary<int, ITjsVariant>();

        public Dictionary<int, IExpression> Expressions { get; set; } =
            new Dictionary<int, IExpression>();

        public DecompileContext(CodeObject obj)
        {
            Object = obj;
            if (obj.ContextType == TjsContextType.TopLevel || obj.Parent == null)
            {
                Expressions[-1] = new ThisPattern(true) {This = obj};
                Expressions[-2] = new ThisPattern(true, true) {This = obj};
            }
            else
            {
                Expressions[-1] = new ThisPattern(false) {This = obj};
                Expressions[-2] = new ThisPattern(false, true) {This = obj};
            }
        }

        public DecompileContext()
        {
        }

        public void AddBranch(int line, BranchType type)
        {
            if (!Branches.ContainsKey(line))
            {
                Branches[line] = new HashSet<BranchType>();
            }

            Branches[line].Add(type);
        }

        public bool ContainsBranch(int line, BranchType type)
        {
            if (!Branches.ContainsKey(line))
            {
                return false;
            }

            return Branches[line].Contains(type);
        }

        public TjsVarType GetSlotType(int slot)
        {
            if (Vars.ContainsKey(slot))
            {
                return Vars[slot].Type;
            }

            return TjsVarType.Null;
        }

        public bool DetectPattern(List<Instruction> instructions, int offset, out IPattern pattern)
        {
            foreach (var detect in Detectors)
            {
                var result = detect(instructions, offset, this);
                if (result != null)
                {
                    pattern = result;
                    return true;
                }
            }

            InstructionQueue.Add(instructions[offset]);
            pattern = null;
            return false;
        }

        /// <summary>
        /// Pop Instruction Queue
        /// </summary>
        /// <param name="slots"></param>
        /// <param name="clear"></param>
        public void PopExpressionPatterns(List<short> slots = null, bool clear = true)
        {
            if (slots != null && slots.Count > 0 && Patterns.Count > 0)
            {
                for (int i = Patterns.Count - 1; i > 0; i--)
                {
                    if (Patterns[i] is IExpression exp)
                    {
                        if (slots.Contains(exp.Slot))
                        {
                            Expressions[exp.Slot] = exp;
                            slots.Remove(exp.Slot);
                            if (slots.Count == 0)
                            {
                                break;
                            }
                        }
                    }
                }
            }

            if (InstructionQueue != null && InstructionQueue.Count > 0)
            {
                var offset = 0;
                while (offset < InstructionQueue.Count)
                {
                    switch (InstructionQueue[offset].OpCode)
                    {
                        case OpCode.GPD: //get
                            var p = ChainGetPattern.Match(InstructionQueue, offset, this);
                            Expressions[p.Slot] = p;
                            offset += p.Length;
                            break;
                        case OpCode.CONST: //const
                            var c = ConstPattern.Match(InstructionQueue, offset, this);
                            Expressions[c.Slot] = c;
                            offset += c.Length;
                            break;
                        case OpCode.CP: //fetch param
                            var l = LocalPattern.Match(InstructionQueue, offset, this);
                            Expressions[l.Slot] = l;
                            break;
                        default:
                            Debug.WriteLine($"Ignore {InstructionQueue[offset]}");
                            offset++;
                            break;
                    }
                }

                if (clear)
                {
                    InstructionQueue.Clear();
                }
            }

            return;
        }

        public void TypeInferScan(List<Instruction> instructions, int i, int slot, bool up = false)
        {
            if (up)
            {
            }

            for (int j = i; j < instructions.Count; j++)
            {
                var ins = instructions[j];
            }
        }

        #region Decompiler Core

        public void ScanBlocks(List<Instruction> instructions)
        {
            ExitBlock = new Block(instructions.Count) {End = instructions.Count};

            int currentAddr = 0;
            var block = GetOrCreateBlockAt(currentAddr);
            Stack<int> workList = new Stack<int>();
            workList.Push(currentAddr);
            while (workList.Count > 0)
            {
                currentAddr = workList.Pop();
                block = GetOrCreateBlockAt(currentAddr);

                NEXT:
                Instruction label = null;
                Instruction branch = null;
                for (int i = currentAddr + 1; i < instructions.Count; i++)
                {
                    var ins = instructions[i];
                    if (label == null && ins.JumpedFrom != null)
                    {
                        if (ins.Line > currentAddr)
                        {
                            label = ins;
                        }
                    }

                    if (branch == null && (ins.OpCode.IsJump() || ins.OpCode == OpCode.RET))
                    {
                        branch = ins;
                    }

                    if (label != null && branch != null)
                    {
                        break;
                    }
                }

                //if (label == null)
                //{
                //    //should reach end now
                //    block.End = instructions.Count - 1;
                //    continue;
                //}

                var addrAfterBranch = branch.Line + 1;
                if (label != null && label.Line < addrAfterBranch) //Take from start to label as a straight block
                {
                    block.End = label.Line - 1;
                    currentAddr = label.Line;
                    var nextBlock1 = GetOrCreateBlockAt(currentAddr);
                    nextBlock1.From.TryAdd(block);
                    block.To.TryAdd(nextBlock1);
                    block = nextBlock1;
                    goto NEXT;
                }

                block.End = addrAfterBranch - 1; //current block: before branch
                if (branch.OpCode == OpCode.RET)
                {
                    ExitBlock.From.TryAdd(block);
                    block.To.TryAdd(ExitBlock);
                    continue;
                }

                var gotoLine = ((JumpData) branch.Data).Goto.Line;
                // ReSharper disable once SimplifyLinqExpression
                if (!Blocks.Any(b => b.Start == gotoLine))
                {
                    workList.Push(gotoLine); //stashed block: created, process later
                }

                var nextBlock = GetOrCreateBlockAt(gotoLine);
                nextBlock.From.TryAdd(block);
                block.To.TryAdd(nextBlock);

                if (!branch.OpCode.IsJump(true))
                {
                    continue;
                }

                nextBlock = GetOrCreateBlockAt(addrAfterBranch); //next block: right after branch (if not jumped)
                nextBlock.From.TryAdd(block);
                block.To.TryAdd(nextBlock);
                block = nextBlock;
                currentAddr = block.Start;
                goto NEXT;
            }

            Blocks.TryAdd(ExitBlock);
            Blocks.Sort((b1, b2) => b1.Start - b2.Start);

            if (Blocks.Count > 0)
            {
                //TJS2 is a simple language, the entry block is always the start block
                EntryBlock = Blocks[0];
            }
        }

        internal Block GetOrCreateBlockAt(int line)
        {
            var block = Blocks.FirstOrDefault(b => b.Start == line);
            if (block == null)
            {
                block = new Block(line);
                Blocks.Add(block);
            }

            return block;
        }

        /// <summary>
        /// Connect to SIBYL SYSTEM and fetch Dominators
        /// </summary>
        /// REF: <value>https://en.wikipedia.org/wiki/Dominator_(graph_theory)</value>
        internal void ComputeDominators()
        {
            if (Blocks.Count == 0)
            {
                return;
            }

            Blocks.Sort((b1, b2) => b1.Start - b2.Start);

            for (int i = 0; i < Blocks.Count; i++)
            {
                var b = Blocks[i];
                b.Id = i;
                b.Dominator = new BitArray(Blocks.Count);
                b.Dominator.SetAll(true);
                b.PostDominator = new BitArray(Blocks.Count);
                b.PostDominator.SetAll(true);
            }

            //Dominators
            var block = EntryBlock;
            block.Dominator.SetAll(false);
            block.Dominator[block.Id] = true;

            var temp = new BitArray(Blocks.Count);
            bool changed = true;

            while (changed)
            {
                changed = false;
                foreach (var bl in Blocks)
                {
                    if (bl == EntryBlock)
                    {
                        continue;
                    }

                    foreach (var pred in bl.From)
                    {
                        temp.SetAll(false);
                        temp.Or(bl.Dominator);
                        bl.Dominator.And(pred.Dominator);
                        bl.Dominator[bl.Id] = true;
                        if (!bl.Dominator.SameAs(temp))
                        {
                            changed = true;
                        }
                    }
                }
            }

            //PostDominators
            block = ExitBlock;
            block.PostDominator.SetAll(false);
            block.PostDominator[block.Id] = true;

            temp = new BitArray(Blocks.Count);
            changed = true;

            while (changed)
            {
                changed = false;
                for (var i = Blocks.Count - 1; i >= 0; i--)
                {
                    var bl = Blocks[i];
                    foreach (var pred in bl.To)
                    {
                        temp.SetAll(false);
                        temp.Or(bl.PostDominator);
                        bl.PostDominator.And(pred.PostDominator);
                        bl.PostDominator[bl.Id] = true;
                        if (!bl.PostDominator.SameAs(temp))
                        {
                            changed = true;
                        }
                    }
                }
            }
        }

        internal void ComputeNaturalLoops()
        {
            foreach (var block in Blocks)
            {
                if (block == EntryBlock)
                {
                    continue;
                }

                foreach (var to in block.To)
                {
                    // Every successor that dominates its predecessor
                    // must be the header of a loop.
                    // That is, block -> succ is a back edge.
                    if (block.Dominator[to.Id])
                    {
                        var natureLoop = NaturalLoopForEdge(to, block);
                        if (natureLoop != null)
                        {
                            LoopSet.Add(natureLoop);
                        }
                    }
                }
            }
        }

        private Loop NaturalLoopForEdge(Block head, Block tail)
        {
            if (head == null || tail == null)
            {
                return null;
            }

            Stack<Block> workList = new Stack<Block>();
            Loop loop = new Loop {Header = head};

            loop.Blocks.Add(head);

            if (head != tail)
            {
                loop.Blocks.Add(tail);
                workList.Push(tail);
            }

            while (workList.Count > 0)
            {
                var block = workList.Pop();
                foreach (var pred in block.From)
                {
                    if (!loop.Blocks.Contains(pred))
                    {
                        loop.Blocks.Add(pred);
                        workList.Push(pred);
                    }
                }
            }

            return loop;
        }

        /// <summary>
        /// Sort loopSet from innermost loop to outermost loop
        /// </summary>
        internal void LoopSetSort()
        {
            LoopSet.Sort((l1, l2) => l1.Blocks.Count - l2.Blocks.Count);
            for (var i = 0; i < LoopSet.Count; i++)
            {
                var loop = LoopSet[i];
                for (int j = i + 1; j < LoopSet.Count; j++)
                {
                    if (LoopSet[j].Blocks.Contains(loop.Header))
                    {
                        LoopSet[j].Children.Add(loop);
                        loop.Parent = LoopSet[j];
                        break;
                    }
                }
            }

            List<Loop> newLoopSet = new List<Loop>(LoopSet.Count);
            foreach (var parent in LoopSet.Where(l => l.Parent == null))
            {
                TravelLoop(newLoopSet, parent);
            }

            LoopSet = newLoopSet;
        }

        private void TravelLoop(List<Loop> set, Loop loop)
        {
            if (!set.Contains(loop))
            {
                set.Add(loop);
            }

            foreach (var child in loop.Children)
            {
                TravelLoop(set, child);
            }
        }

        private Block FindBreak(Loop loop)
        {
            BitArray b = new BitArray(ExitBlock.PostDominator.Length);
            b.SetAll(true);
            foreach (var block in loop.Blocks)
            {
                b.And(block.PostDominator);
                b[block.Id] = false; //block after break can not still stay in the loop
            }

            var id = b.FirstIndexOf(true);
            if (id >= 0)
            {
                return Blocks[id];
            }

            return null;
        }

        internal void IntervalAnalysisDoWhilePass()
        {
            LoopSetSort();
            foreach (var loop in LoopSet)
            {
                var dw = new DoWhilePattern();
                dw.Condition = loop.FindCondition();
                dw.Content = loop.Blocks;
                dw.Break = FindBreak(loop);
                dw.Continue = null;

                var cont = loop.Blocks.LastOrDefault();

                if (cont != null)
                {
                    if (cont.Statements.LastOrDefault() is IfPattern i)
                    {
                        if (i.To == loop.Header)
                        {
                            dw.Continue = cont;
                        }
                    }
                }

                loop.Header.Statements.Clear();
                loop.Header.Statements.Add(dw);
                StructureBreakContinue(dw, dw.Continue, dw.Break);
            }
        }

        internal void StructureBreakContinue(IBranch stmt, Block continueBlock, Block breakBlock)
        {
            switch (stmt)
            {
                case IfPattern ifStmt:
                    if (ifStmt.Else != null && ifStmt.Else.Count > 0)
                    {
                        foreach (var el in ifStmt.Else)
                        {
                            foreach (var elStmt in el.Statements)
                            {
                                if (elStmt is IBranch b)
                                {
                                    StructureBreakContinue(b, continueBlock, breakBlock);
                                }
                            }
                        }
                    }

                    if (ifStmt.Content != null && ifStmt.Content.Count > 0)
                    {
                        foreach (var el in ifStmt.Content)
                        {
                            foreach (var elStmt in el.Statements)
                            {
                                if (elStmt is IBranch b)
                                {
                                    StructureBreakContinue(b, continueBlock, breakBlock);
                                }
                            }
                        }
                    }

                    break;
            }
        }

        internal bool StructureIfElse(Block block)
        {
            if (block.To.Count != 2)
            {
                return false;
            }

            var trueB = block.To[0];
            var falseB = block.To[1];
            bool hasElse = true;
            if (trueB.To.Count != 1)
            {
                return false;
            }

            if (trueB.To[0] == falseB)
            {
                hasElse = false;
            }
            else
            {
                if (falseB.To.Count != 1)
                {
                    return false;
                }

                if (falseB.To[0] != trueB.To[0])
                {
                    return false;
                }

                hasElse = true;
            }


            IfPattern i = new IfPattern();
            //i.Condition
            i.Content.Add(trueB);

            RemoveLastGoto(trueB, trueB.To[0]);
            if (hasElse)
            {
                i.Else.Add(falseB);
                RemoveLastGoto(falseB, falseB.To[0]);
            }

            return true;
        }

        private void RemoveLastGoto(Block from, Block to)
        {
            var gt = from.Statements.LastOrDefault(p => p is GotoPattern g);
            if (gt != null)
            {
                from.Statements.Remove(gt);
            }
        }

        private void FillInstructionIntoBlock(Block block, List<Instruction> instructions)
        {
            block.Statements.Clear();
            int i = block.Start;
            while (i <= block.End && i < instructions.Count)
            {
                block.Statements.Add(new InstructionPattern(instructions[i]));
                i++;
            }
        }

        private void FillInBlock(Block block, List<Instruction> instructions)
        {
            block.Statements.Clear();
            int i = block.Start;
            while (i <= block.End && i < instructions.Count)
            {
                DetectPattern(instructions, i, out var p);
                if (p != null)
                {
                    block.Statements.Add(p);
                    i += p.Length;
                }
                else
                {
                    i++;
                }
            }
        }

        internal void FillInBlocks(List<Instruction> instructions)
        {
            foreach (var block in Blocks)
            {
                //FillInBlock(block, instructions);
                FillInstructionIntoBlock(block, instructions);
            }
        }

        internal void LifetimeAnalysis()
        {
            //Pass 1
            foreach (var block in Blocks)
            {
                block.Use = new HashSet<int>();
                block.Def = new HashSet<int>();
                block.Input = new HashSet<int>();
                block.Output = new HashSet<int>();
                for (int i = block.Statements.Count - 1; i >= 0; i--)
                {
                    if (!(block.Statements[i] is ITerminal ins))
                    {
                        continue;
                    }

                    ins.ComputeUseDefs();

                    ins.LiveOut = ins.Read;
                    ins.Dead.AddRange(ins.Write.Except(ins.LiveOut));
                    block.Use.AddRange(ins.LiveOut);
                    block.Use.AddRange(block.Use.Except(ins.Dead));
                    block.Def.AddRange(ins.Dead);
                    block.Def.AddRange(block.Def.Except(ins.LiveOut));
                }
            }

            //Pass 2
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in Blocks)
                {
                    var output = new HashSet<int>(block.Def);
                    foreach (var succ in block.To)
                    {
                        output.AddRange(succ.Input);
                    }

                    var input = new HashSet<int>(block.Use);
                    input.AddRange(output.Except(block.Def));
                    if (!input.SetEquals(block.Input) || !output.SetEquals(block.Output))
                    {
                        changed = true;
                        block.Input = input;
                        block.Output = output;
                    }
                }
            }

            //Pass 3
            foreach (var block in Blocks)
            {
                var live = new HashSet<int>();
                foreach (var succ in block.To)
                {
                    live.AddRange(succ.Input);
                }

                for (int i = block.Statements.Count - 1; i >= 0; i--)
                {
                    if (!(block.Statements[i] is ITerminal ins))
                    {
                        continue;
                    }

                    var newLive = new HashSet<int>(ins.LiveOut);
                    newLive.AddRange(live.Except(ins.Dead));
                    ins.LiveOut = live;
                    live = newLive;
                }
            }
        }

        #endregion
    }
}