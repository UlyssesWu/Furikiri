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

        public DecompileContext(CodeObject obj, List<DetectHandler> detectors)
        {
            Object = obj;
            Object = obj;
            Detector = detectors;
        }

        public DecompileContext()
        { }

        public Dictionary<int, IExpressionPattern> PopExpressionPatterns(bool clear = true)
        {
            Dictionary<int, IExpressionPattern> expressions = new Dictionary<int, IExpressionPattern>();
            if (InstructionQueue == null || InstructionQueue.Count == 0)
            {
                return expressions;
            }
            var offset = 0;
            while (offset < InstructionQueue.Count)
            {
                switch (InstructionQueue[offset].OpCode)
                {
                    case OpCode.GPD: //get
                        var p = ChainGetPattern.TryMatch(InstructionQueue, offset, this);
                        expressions[p.Slot] = p;
                        offset += p.Length;
                        break;
                    case OpCode.CONST: //const
                        var c = ConstPattern.TryMatch(InstructionQueue, offset, this);
                        expressions[c.Slot] = c;
                        offset += c.Length;
                        break;
                    case OpCode.CP: //fetch param
                        //if (InstructionQueue[offset].)
                        //{
                            
                        //}
                        //var cp = 
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

            return expressions;
        }
    }
}
