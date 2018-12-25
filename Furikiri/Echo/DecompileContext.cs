using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Furikiri.Echo.Patterns;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    internal class DecompileContext
    {
        public Dictionary<TjsVarType, int> ParamCounts = new Dictionary<TjsVarType, int>()
        {
            {TjsVarType.Int, 0 },
            {TjsVarType.Real, 0 },
            {TjsVarType.String, 0 },
            {TjsVarType.Octet, 0 },
            {TjsVarType.Object, 0 },
            {TjsVarType.Void, 0 },
            {TjsVarType.Null, 0 },
        }; 

        public CodeObject Object { get; set; }
        public List<DetectHandler> Detector { get; set; }
        public List<Instruction> InstructionQueue { get; set; } = new List<Instruction>();
        public List<IPattern> Blocks { get; set; } = new List<IPattern>();
        public Dictionary<int, TjsVarType> TypeInfo { get; set; } = new Dictionary<int, TjsVarType>();
        public Dictionary<int, IExpressionPattern> Expressions { get; set; } = new Dictionary<int, IExpressionPattern>();
        public DecompileContext(CodeObject obj, List<DetectHandler> detectors)
        {
            Object = obj;
            Object = obj;
            Detector = detectors;
        }

        public DecompileContext()
        { }

        public TjsVarType GetSlotType(int slot)
        {
            if (TypeInfo.ContainsKey(slot))
            {
                return TypeInfo[slot];
            }

            return TjsVarType.Null;
        }

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
