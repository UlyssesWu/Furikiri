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
            Dictionary<Block, List<IAstNode>> blockStmts = new Dictionary<Block, List<IAstNode>>();
            foreach (var block in context.Blocks)
            {
                var newStmts = new List<IAstNode>();
                var loop = context.LoopSet.FirstOrDefault(l => l.Header == block);
                if (loop != null)
                {
                    // Only add loop statement if this loop is not a child of another loop
                    // Child loops will be included in their parent loop's body naturally
                    if (loop.Parent == null)
                    {
                        newStmts.Add(loop.LoopLogic.ToStatement());
                        block.Hidden = false; //TODO: temp fix
                    }
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