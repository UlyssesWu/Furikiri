using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a.b()</example>
    /// </summary>
    class CallPattern : ITjsPattern
    {
        public int Length { get; }

        public static CallPattern TryMatch(List<Instruction> codes, int i, DecompileContext context)
        {
            return null;
        }
    }
}
