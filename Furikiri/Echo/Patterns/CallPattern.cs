using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a.b()</example>
    /// </summary>
    class CallPattern : IPattern
    {
        public int Length { get; }

        public static CallPattern TryMatch(List<Instruction> codes, int i, DecompileContext context)
        {
            ChainGetPattern get = ChainGetPattern.TryMatch(codes, i, context);
            if (get == null)
            {
                return null;
            }

            i += get.Length;
            if (codes[i].OpCode == OpCode.CALLD)
            {
                CallPattern call = new CallPattern();
            }

            return null;
        }

        public List<IExpressionPattern> Parameters { get; set; } = new List<IExpressionPattern>();
    }
}
