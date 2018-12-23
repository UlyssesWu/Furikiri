using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;
using Tjs2.Engine;

namespace Furikiri.Echo
{
    interface ITjsPattern
    {
        bool TryMatch(List<Instruction> codes, int index);
        //ExprNode Match(in byte[] codes, int index);
    }
}
