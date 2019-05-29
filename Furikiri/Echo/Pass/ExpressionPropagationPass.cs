using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    class ExpressionPropagationPass : IPass
    {
        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            foreach (var b in context.Blocks)
            {
                ExpressionPropagation(b);
            }

            return statement;
        }

        private void ExpressionPropagation(Block block)
        {

        }
    }
}
