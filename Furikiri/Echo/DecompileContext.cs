using System.Collections.Generic;
using System.Diagnostics;
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

        public CodeObject Object { get; set; }
        public List<DetectHandler> Detectors { get; set; }
        public List<DetectHandler> BranchDetectors { get; set; }
        public List<Instruction> InstructionQueue { get; set; } = new List<Instruction>();
        public List<IPattern> Blocks { get; set; } = new List<IPattern>();
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
            if (slots != null && slots.Count > 0 && Blocks.Count > 0)
            {
                for (int i = Blocks.Count - 1; i > 0; i--)
                {
                    if (Blocks[i] is IExpressionPattern exp)
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