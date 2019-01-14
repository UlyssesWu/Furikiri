using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a.b()</example>
    /// </summary>
    class CallPattern : IExpression
    {
        public bool Terminal { get; set; }
        public int Length => 1;
        public TjsVarType Type { get; set; }
        public short Slot { get; set; }
        public IExpression Caller { get; set; }

        public string Method { get; set; }

        //public string CallerName { get; set; }
        public List<IExpression> Parameters { get; set; } = new List<IExpression>();

        public static CallPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            if (codes[i].OpCode == OpCode.CALLD)
            {
                CallPattern call = new CallPattern();
                context.PopExpressionPatterns(codes[i].GetRelatedSlots());
                call.Slot = codes[i].GetRegisterSlot(0);
                if (call.Slot == 0)
                {
                    call.Terminal = true;
                }

                var callerSlot = codes[i].GetRegisterSlot(1);
                call.Caller = context.Expressions[callerSlot];

                //switch (callerSlot)
                //{
                //    case 0:
                //        call.Caller = null;
                //        call.CallerName = "";
                //        break;
                //    case Const.This:
                //        call.Caller = null;
                //        call.CallerName = "this";
                //        break;
                //    case Const.ThisProxy:
                //        call.Caller = null;
                //        call.CallerName = "";
                //        break;
                //    default:
                //        call.Caller = context.Expressions[callerSlot];
                //        call.CallerName = null;
                //        break;
                //}

                call.Method = codes[i].Data.AsString();
                var paramCount = codes[i].GetRegisterSlot(3);
                if (paramCount == -1)
                {
                    //...
                    //do nothing
                }
                else
                {
                    for (int j = 0; j < paramCount; j++)
                    {
                        var pSlot = codes[i].GetRegisterSlot(4 + j);
                        call.Parameters.Add(context.Expressions[pSlot]);
                    }
                }

                return call;
            }

            return null;
        }

        public override string ToString()
        {
            var param = string.Join(", ", Parameters);
            var caller = Caller?.ToString();
            if (string.IsNullOrEmpty(caller))
            {
                return $"{Method}({param}){Terminal.Term()}";
            }

            return $"{caller}.{Method}({param}){Terminal.Term()}";
        }
    }
}