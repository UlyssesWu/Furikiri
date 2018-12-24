using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// parameter or local var
    /// <example>var a / x(a)</example>
    /// </summary>
    class LocalPattern : IExpressionPattern
    {
        public bool IsParameter { get; private set; } = false;
        public int Length => 1;
        public int Slot { get; set; }

        public static LocalPattern TryMatch(List<Instruction> codes, int i, DecompileContext context)
        {
            var argCount = context.Object.FuncDeclArgCount;
            var varCount = context.Object.MaxVariableCount;
            if (codes[i].OpCode == OpCode.CP)
            {
                var dst = codes[i].Registers[0].GetSlot();
                var src = codes[i].Registers[1].GetSlot();
                int local = 0;
                int immediate = 0;
                if (dst < Const.ThisProxy && src > Const.Resource)
                {
                    local = dst;
                    immediate = src;
                }
                else if (src < Const.ThisProxy && dst > Const.Resource)
                {
                    //usually fetch parameter
                    local = src;
                    immediate = dst;
                }

                //TODO:

            }

            return null;
        }
    }
}
