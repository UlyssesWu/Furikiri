using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    class StatementCollectPass : IPass
    {
        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            //TODO:
            Dictionary<Block, List<IAstNode>> blockStmts = new Dictionary<Block, List<IAstNode>>();
            foreach (var block in context.Blocks)
            {
                var newStmts = new List<IAstNode>();
                var loop = context.LoopSet.FirstOrDefault(l => l.Header == block);
                if (loop != null)
                {
                    newStmts.Add(loop.LoopStatement);
                }
                else
                {
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
                }

                blockStmts[block] = newStmts;
            }

            foreach (var block in context.Blocks)
            {
                if (block.Hidden)
                {
                    continue;
                }

                statement.Statements.AddRange(blockStmts[block]);
            }

            return statement;
        }
    }
}