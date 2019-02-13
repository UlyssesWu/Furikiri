using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    interface IPass
    {
        BlockStatement Process(DecompileContext context, BlockStatement statement);
    }
}