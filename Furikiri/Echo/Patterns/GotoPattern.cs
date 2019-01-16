using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.Patterns
{
    class GotoPattern : IBranch
    {
        public bool Terminal { get; set; }
        public int Length { get; }
        public BranchType BranchType => BranchType.Goto;
        public List<Block> Content { get; }
        public IExpression Condition { get; }
        public Block Dst { get; set; }
    }
}