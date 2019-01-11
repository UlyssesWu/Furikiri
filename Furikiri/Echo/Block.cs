using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    class Block
    {
        public int Start { get; set; }
        public int End { get; set; }
        public Instruction Entry { get; set; }
        public Instruction Exit { get; set; }
        public BranchType BranchType { get; set; }
        public IBranchPattern Pattern { get; set; }
    }
}
