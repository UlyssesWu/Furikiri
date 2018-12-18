using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    class TjsFormatException : Exception
    {
        public TjsFormatException(string info) : base(info)
        { }
    }
}
