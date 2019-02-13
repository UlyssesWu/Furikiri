using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Furikiri.AST;
using Furikiri.AST.Expressions;
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
        public BitArray Dominator { get; set; }

        /// <summary>
        /// Post-Dominator
        /// </summary>
        /// REF: https://en.wikipedia.org/wiki/Dominator_(graph_theory)#Postdominance
        public BitArray PostDominator { get; set; }

        public HashSet<int> Def { get; set; }
        public HashSet<int> Use { get; set; }
        public HashSet<int> Input { get; set; }
        public HashSet<int> Output { get; set; }

        public List<Instruction> Instructions { get; set; } = new List<Instruction>();
        public List<IAstNode> Statements { get; set; } = null;

        public Dictionary<int, IPattern> RegisterMap { get; set; } =
            new Dictionary<int, IPattern>();

        public Block(int start)
        {
            Start = start;
        }

        public bool IsInBlock(int line)
        {
            return line >= Start && line <= End;
        }

        public override string ToString()
        {
            return base.ToString();
        }
    }
}