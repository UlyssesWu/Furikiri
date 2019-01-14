using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo
{
    /// <summary>
    /// Loop
    /// </summary>
    class Loop
    {
        public Block Header { get; set; }
        public List<Block> Blocks { get; set; } = new List<Block>();
    }
}