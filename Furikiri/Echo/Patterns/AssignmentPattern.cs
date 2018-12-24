using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a.b = c</example>
    /// </summary>
    class AssignmentPattern : IPattern
    {
        public int Length { get; }

        public static AssignmentPattern TryMatch(List<Instruction> codes, int i, DecompileContext context)
        {
            var get = ChainGetPattern.TryMatch(codes, i, context);
            return null;
        }
    }
}
