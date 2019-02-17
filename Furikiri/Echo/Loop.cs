using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Furikiri.AST.Statements;

namespace Furikiri.Echo
{
    /// <summary>
    /// Loop
    /// </summary>
    class Loop
    {
        public Loop Parent { get; set; }
        public List<Loop> Children { get; set; } = new List<Loop>();
        public Block Header { get; set; }

        public List<Block> Blocks { get; set; } = new List<Block>();

        public Statement LoopStatement { get; set; }

        public bool Contains(Block b)
        {
            return Blocks.Contains(b);
        }

        public int Exit => Blocks.Last().End + 1;
    }
}