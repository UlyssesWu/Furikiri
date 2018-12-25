using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a.b()</example>
    /// </summary>
    class CallPattern : IExpressionPattern
    {
        public bool Terminal { get; set; }
        public int Length => 1;
        public TjsVarType Type { get; set; }
        public short Slot { get; set; }
        public IExpressionPattern Caller { get; set; }
        public string Method { get; set; }
        public string CallerName { get; set; }
        public List<IExpressionPattern> Parameters { get; set; } = new List<IExpressionPattern>();
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

                switch (callerSlot)
                {
                    case 0:
                        call.Caller = null;
                        call.CallerName = "";
                        break;
                    case Const.This:
                        call.Caller = null;
                        call.CallerName = "this";
                        break;
                    case Const.ThisProxy:
                        call.Caller = null;
                        call.CallerName = "";
                        break;
                    default:
                        call.Caller = context.Expressions[callerSlot];
                        call.CallerName = null;
                        break;
                }

                var data = (OperandData) codes[i].Data;
                var m = (TjsString) data.Variant;
                call.Method = m;
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
            var caller = CallerName ?? Caller?.ToString();
            var param = string.Join(", ", Parameters);
            if (string.IsNullOrEmpty(caller))
            {
                return $"{Method}({param}){Terminal.TermSymbol()}";
            }
            return $"{caller}.{Method}({param}){Terminal.TermSymbol()}";
        }
    }
}
