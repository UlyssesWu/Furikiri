using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    /// <summary>
    /// Block
    /// </summary>
    [DebuggerDisplay("{Start}-{End} ({Length})")]
    class Block
    {
        public int Id { get; set; }
        public List<Block> From { get; set; } = new List<Block>();
        public List<Block> To { get; set; } = new List<Block>();
        public int Start { get; set; }
        public int End { get; set; }
        public int Length => End - Start + 1;
        public Instruction Entry { get; set; }
        public Instruction Exit { get; set; }
        public BranchType BranchType { get; set; }
        public BitArray Dominator { get; set; }

        public Block(int start)
        {
            Start = start;
        }

        public bool IsInBlock(int line)
        {
            return line >= Start && line <= End;
        }

        public bool IsInBlock(Instruction ins)
        {
            return ins.Offset >= Entry.Offset && ins.Offset <= Exit.Offset;
        }
    }
}