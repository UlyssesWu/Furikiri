using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.Patterns
{
    class DoWhilePattern : IBranch
    {
        public bool Terminal { get; set; }
        public int Length { get; }
        public BranchType BranchType => BranchType.DoWhile;
        public List<Block> Content { get; set; } = new List<Block>();
        public Block Continue { get; set; }
        public Block Break { get; set; }
        public IExpression Condition { get; set; }
    }
}