using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    class InstructionContainer : IPattern
    {
        public int Length { get; }
        public List<Instruction> Instructions { get; set; } = new List<Instruction>();
    }
}