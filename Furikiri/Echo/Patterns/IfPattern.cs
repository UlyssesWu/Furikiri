using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    class IfPattern : IBranchPattern
    {
        public bool Terminal { get; set; }
        public int Length { get; }
        public List<IPattern> If { get; set; }
        public List<IPattern> Else { get; set; }

        public static IfPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            return null;
            if (codes[i].OpCode == OpCode.TT)
            {
                var exp = context.Expressions[codes[i].GetRegisterSlot(0)];
                if (i+1 < codes.Count && codes[i+1].OpCode.IsJump(false)) //if
                {
                    if (exp != null)
                    {
                        exp.Terminal = false;
                    }
                }
            }

            return null;
        }
    }
}
