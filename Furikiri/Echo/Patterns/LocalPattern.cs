using System;
using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// parameter or local var
    /// <example>var a / x(a)</example>
    /// </summary>
    class LocalPattern : IExpression
    {
        public bool IsParameter { get; private set; } = false;
        public int Length => 1;
        public TjsVarType Type { get; set; }
        public short Slot { get; set; }
        public string Name { get; set; }
        public string DefaultName => Name ?? $"{(IsParameter ? "p" : "v")}{Math.Abs(Slot) + 2}";
        public LocalPattern(bool isParam, short slot)
        {
            IsParameter = isParam;
            Slot = slot;
        }

        internal LocalPattern()
        { }

        public static LocalPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            //cp dst, src
            var argCount = context.Object.FuncDeclArgCount;
            var varCount = context.Object.MaxVariableCount;
            if (codes[i].OpCode == OpCode.CP)
            {
                var dst = codes[i].Registers[0].GetSlot();
                var src = codes[i].Registers[1].GetSlot();
                if (dst < Const.ThisProxy && src > Const.Resource)
                {
                    //set var
                    //local = dst;
                    //immediate = src;
                    return null;
                }

                LocalPattern l = new LocalPattern {Slot = dst};
                if (src < Const.ThisProxy && dst > Const.Resource)
                {
                    //usually fetch parameter from src, save to dst
                    if (src > Const.ThisProxy - argCount) //param
                    {
                        l.IsParameter = true;
                        l.Type = context.GetSlotType(src);
                    }
                }
                return l;
            }

            return null;
        }

        public override string ToString()
        {
            if (IsParameter)
            {
                return DefaultName;
            }
            return $"var {DefaultName}";
        }
    }
}
