using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.Patterns
{
    class BranchPattern : IPattern
    {
        public bool Terminal { get; }
        public int Length { get; }
    }
}
