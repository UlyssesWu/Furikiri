using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

        public IExpression FindCondition()
        {
            if (Blocks.Count <= 0)
            {
                return null;
            }

            var last = Blocks.Last();
            if (last.Statements.Count <= 0)
            {
                return null;
            }

            return (IExpression) last.Statements.LastOrDefault(s => s is IExpression && s.IsCompareStatement());
        }
    }
}