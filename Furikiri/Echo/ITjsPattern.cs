using System;
using System.Collections.Generic;
using System.Text;
using Tjs2.Engine;

namespace Furikiri.Echo
{
    interface ITjsPattern
    {
        bool TryMatch(in byte[] codes, int index, out ExprNode node);
        //ExprNode Match(in byte[] codes, int index);
    }
}
