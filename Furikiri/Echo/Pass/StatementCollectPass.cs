using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    class StatementCollectPass : IPass
    {
        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            // Pre-materialize nested loops so parent loop body resolution can include them
            // as statements on child headers, while keeping top-level loop collection unchanged.
            foreach (var nested in context.LoopSet.Where(l => l.Parent != null).OrderBy(l => l.Blocks.Count))
            {
                if (nested.LoopLogic == null || nested.Header == null)
                {
                    continue;
                }
                if (!(nested.LoopLogic is ForLogic))
                {
                    continue;
                }

                nested.Header.Statements = new List<IAstNode> { nested.LoopLogic.ToStatement() };
                nested.Header.Hidden = false;
            }

            Dictionary<Block, List<IAstNode>> blockStmts = new Dictionary<Block, List<IAstNode>>();
            foreach (var block in context.Blocks)
            {
                var newStmts = new List<IAstNode>();
                var loop = context.LoopSet.FirstOrDefault(l => l.Header == block);
                if (loop != null)
                {
                    if (loop.Parent == null)
                    {
                        newStmts.Add(loop.LoopLogic.ToStatement());
                    }
                    block.Hidden = false; //TODO: temp fix
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