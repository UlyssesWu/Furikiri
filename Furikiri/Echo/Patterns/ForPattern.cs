using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    class ForPattern : IPattern
    {
        public bool Terminal { get; set; }
        public int Length { get; }

        public static ForPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            return null;
        }
    }
}