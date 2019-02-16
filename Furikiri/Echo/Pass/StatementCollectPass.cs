using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    class StatementCollectPass : IPass
    {
        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            foreach (var block in context.Blocks)
            {
                var newStmts = new List<IAstNode>();
                foreach (var node in block.Statements)
                {
                    switch (node)
                    {
                        case GotoExpression _:
                        case ConditionExpression _:
                            break;
                        case Expression expr:
                            newStmts.Add(new ExpressionStatement(expr));
                            break;
                        case Statement stmt:
                            newStmts.Add(stmt);
                            break;
                    }
                }

                block.Statements = newStmts;
            }

            foreach (var block in context.Blocks)
            {
                if (block.Hidden)
                {
                    continue;
                }

                statement.Statements.AddRange(block.Statements.OfType<Statement>());
            }

            return statement;
        }
    }
}