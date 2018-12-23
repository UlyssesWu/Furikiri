using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    internal class Property
    {
        public CodeObject Parent { get; set; }
        public string Name { get; set; }
        public CodeObject Getter { get; set; }
        public CodeObject Setter { get; set; }
    }
}
