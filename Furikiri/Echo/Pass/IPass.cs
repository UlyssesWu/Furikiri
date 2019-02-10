using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.AST.Statement;

namespace Furikiri.Echo.Pass
{
    interface IPass
    {
        BlockStatement Process(DecompileContext context, BlockStatement statement);
    }
}