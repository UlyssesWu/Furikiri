using Furikiri.AST.Expressions;
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
            foreach (var statement in block.Statements)
            {
                if (statement is ExpressionStatement exp && exp.Expression is PhiExpression phi)
                {
                    //TODO:
                }
            }
        }
    }
}