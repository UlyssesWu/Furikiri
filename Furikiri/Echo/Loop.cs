using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Furikiri.AST.Statements;
using Furikiri.Echo.Logical;

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

        public ILogical LoopLogic { get; set; }

        public List<Block> Body { get; set; } = new List<Block>();
        public Block Break { get; set; } 
        public Block Continue { get; set; }
        
        /// <summary>
        /// Reference to all loops in the context for nested loop resolution
        /// </summary>
        public List<Loop> AllLoops { get; set; }
        
        /// <summary>
        /// Flag to prevent infinite recursion when resolving nested loops
        /// </summary>
        public bool IsBeingResolved { get; set; }

        public bool Contains(Block b)
        {
            return Blocks.Contains(b);
        }

        public int Exit => Blocks.Last().End + 1;
    }
}