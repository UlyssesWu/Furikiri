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

        internal List<Block> Blocks { get; set; } = new List<Block>();
        public CodeObject Object { get; set; }
        public List<DetectHandler> Detectors { get; set; }
        public List<Instruction> InstructionQueue { get; set; } = new List<Instruction>();
        public List<IPattern> Patterns { get; set; } = new List<IPattern>();
        public Dictionary<int, ITjsVariant> Vars { get; set; } = new Dictionary<int, ITjsVariant>();

        public Dictionary<int, IExpressionPattern> Expressions { get; set; } =
            new Dictionary<int, IExpressionPattern>();

        public DecompileContext(CodeObject obj, List<DetectHandler> detectors)
        {
            Object = obj;
            Detectors = detectors;
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

        public void ScanBlocks(List<Instruction> instructions)
        {
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

                    if (branch == null && ins.OpCode.IsJump())
                    {
                        branch = ins;
                    }

                    if (label != null && branch != null)
                    {
                        break;
                    }
                }

                if (label == null || branch == null)
                {
                    //should reach end now
                    block.Length = instructions.Count - block.Start;
                    continue;
                }

                var gotoLine = ((JumpData) branch.Data).Goto.Line;
                var addrAfterBranch = branch.Line + 1;
                if (label.Line < addrAfterBranch) //Take from start to label as a straight block
                {
                    block.Length = label.Line - block.Start;
                    currentAddr = label.Line;
                    var nextBlock1 = GetOrCreateBlockAt(currentAddr);
                    nextBlock1.From.Add(block);
                    block.To.Add(nextBlock1);
                    block = nextBlock1;
                    goto NEXT;
                }

                block.Length = addrAfterBranch - block.Start;
                if (branch.OpCode == OpCode.RET)
                {
                    continue;
                }

                // ReSharper disable once SimplifyLinqExpression
                if (!Blocks.Any(b => b.Start == gotoLine))
                {
                    workList.Push(gotoLine);
                }

                var nextBlock = GetOrCreateBlockAt(gotoLine);
                nextBlock.From.Add(block);
                block.To.Add(nextBlock);

                if (!branch.OpCode.IsJump(true))
                {
                    continue;
                }

                nextBlock = GetOrCreateBlockAt(addrAfterBranch);
                nextBlock.From.Add(block);
                block.To.Add(nextBlock);
                block = nextBlock;
                currentAddr = block.Start;
                goto NEXT;
            }

            Blocks.Sort((b1, b2) => b1.Start - b2.Start);
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
                    if (Patterns[i] is IExpressionPattern exp)
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
    }
}