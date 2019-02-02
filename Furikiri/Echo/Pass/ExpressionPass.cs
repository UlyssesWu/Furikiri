using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Echo.AST;

namespace Furikiri.Echo.Pass
{
    class ExpressionPass : IPass
    {
        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            return statement; //TODO:
        }
    }
}