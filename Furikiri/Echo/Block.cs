using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    [DebuggerDisplay("{Start}-{End} ({Length})")]
    class Block
    {
        public Block Parent { get; set; }
        public List<Block> From { get; set; } = new List<Block>();
        public List<Block> To { get; set; } = new List<Block>();
        public int Start { get; set; }
        public int End { get; set; }
        public int Length { get; set; }
        public Instruction Entry { get; set; }
        public Instruction Exit { get; set; }
        public BranchType BranchType { get; set; }

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