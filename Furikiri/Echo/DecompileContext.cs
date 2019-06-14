using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.Echo.Logical;
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

        internal List<ILogical> Logicals { get; set; } = new List<ILogical>();

        internal Block EntryBlock { get; set; }

        /// <summary>
        /// A fake block for any return
        /// </summary>
        internal Block ExitBlock { get; set; } = new Block(-1);

        internal List<Block> Blocks { get; set; } = new List<Block>();
        /// <summary>
        /// Based on Start, not on Id
        /// </summary>
        internal Dictionary<int, Block> BlockTable { get; private set; }
        internal List<Loop> LoopSet { get; set; } = new List<Loop>();

        public CodeObject Object { get; set; }
        public Dictionary<short, Variable> Vars { get; set; } = new Dictionary<short, Variable>();
        

        internal Dictionary<string, ITjsVariant> RegisteredMembers { get; set; } =
            new Dictionary<string, ITjsVariant>();

        internal Dictionary<Block, List<Expression>> BlockExpressions { get; set; } =
            new Dictionary<Block, List<Expression>>();

        public DecompileContext(CodeObject obj)
        {
            Object = obj;
        }

        public DecompileContext()
        {
        }

        public void UpdateBlockTable()
        {
            BlockTable = new Dictionary<int, Block>(Blocks.Count);
            foreach (var block in Blocks)
            {
                BlockTable[block.Start] = block;
            }
        }

        public TjsVarType GetSlotType(short slot)
        {
            if (Vars.ContainsKey(slot))
            {
                return Vars[slot].VarType;
            }

            return TjsVarType.Null;
        }
        
        #region CFG

        public void BuildCFG(List<Instruction> instructions)
        {
            ScanBlocks(instructions);
            UpdateBlockTable();

            ComputeDominators();
            ComputeNaturalLoops();

            FillInBlocks(instructions);

            LifetimeAnalysis();
        }

        internal void ScanBlocks(List<Instruction> instructions)
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

            foreach (var bl in Blocks)
            {
                bl.From.Sort((b1, b2) => b1.Start - b2.Start);
                bl.To.Sort((b1, b2) => b1.Start - b2.Start);
            }


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
        
        //private void FillInstructionIntoBlock(Block block, List<Instruction> instructions)
        //{
        //    block.Statements.Clear();
        //    int i = block.Start;
        //    while (i <= block.End && i < instructions.Count)
        //    {
        //        block.Statements.Add(new InstructionPattern(instructions[i]) {Block = block});
        //        i++;
        //    }
        //}

        private void FillInstructionIntoBlock(Block block, List<Instruction> instructions)
        {
            block.Instructions.Clear();
            int i = block.Start;
            while (i <= block.End && i < instructions.Count)
            {
                block.Instructions.Add(instructions[i]);
                i++;
            }
        }

        //private void FillInBlock(Block block, List<Instruction> instructions)
        //{
        //    block.Statements.Clear();
        //    int i = block.Start;
        //    while (i <= block.End && i < instructions.Count)
        //    {
        //        DetectPattern(instructions, i, out var p);
        //        if (p != null)
        //        {
        //            block.Statements.Add(p);
        //            i += p.Length;
        //        }
        //        else
        //        {
        //            i++;
        //        }
        //    }
        //}

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
                block.InstructionDatas = new List<InstructionData>(block.Instructions.Count);
                for (int i = block.Instructions.Count - 1; i >= 0; i--)
                {
                    var ins = block.Instructions[i];
                    var insData = new InstructionData(ins);
                    block.InstructionDatas.Add(insData);
                    insData.ComputeUseDef();

                    insData.LiveOut = insData.Read;
                    insData.Dead.AddRange(insData.Write.Except(insData.LiveOut));
                    block.Use.AddRange(insData.LiveOut);
                    block.Use.AddRange(block.Use.Except(insData.Dead));
                    block.Def.AddRange(insData.Dead);
                    block.Def.AddRange(block.Def.Except(insData.LiveOut));
                }

                block.InstructionDatas.Reverse();
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

                for (int i = block.InstructionDatas.Count - 1; i >= 0; i--)
                {
                    var insData = block.InstructionDatas[i];
                    var newLive = new HashSet<int>(insData.LiveOut);
                    newLive.AddRange(live.Except(insData.Dead));
                    insData.LiveOut = live;
                    live = newLive;
                }
            }

            //Pass 4: Set Live In
            foreach (var block in Blocks)
            {
                for (int i = 1; i < block.InstructionDatas.Count; i++)
                {
                    var insData = block.InstructionDatas[i];
                    insData.LiveIn = block.InstructionDatas[i - 1].LiveOut;
                }
            }
        }

        //public void PropagateExpressions()
        //{
        //    foreach (var block in Blocks)
        //    {
        //    }
        //}

        #endregion
    }
}