using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    internal class TryLogic : ILogical
    {
        public Block EnterTry { get; set; }
        public Block ExitTry { get; set; }

        public List<Block> Body { get; set; }

        public Expression CatchClause { get; set; }

        public List<Block> CatchBody { get; set; }

        internal void HideBlocks()
        {
            // Keep entry block visible because it now holds the synthesized TryStatement.
            if (Body != null)
            {
                foreach (var block in Body)
                {
                    block.Hidden = true;
                }
            }
            if (CatchBody != null)
            {
                foreach (var block in CatchBody)
                {
                    block.Hidden = true;
                }
            }
        }

        public Statement ToStatement()
        {
            var tryStatement = new TryStatement();
            
            IAstNode NormalizeNode(IAstNode node)
            {
                if (node is Expression expr)
                {
                    return new ExpressionStatement(expr);
                }
                return node;
            }

            bool IsControlFlowNode(IAstNode node, bool includeCatch)
            {
                if (node is ExpressionStatement es)
                {
                    if (es.Expression is GotoExpression || es.Expression is IJump)
                    {
                        return true;
                    }

                    if (includeCatch && es.Expression is CatchExpression)
                    {
                        return true;
                    }

                    return false;
                }

                if (node is GotoExpression || node is IJump)
                {
                    return true;
                }

                if (includeCatch && node is CatchExpression)
                {
                    return true;
                }

                return false;
            }

            // Build try block
            var tryBlock = new BlockStatement();
            if (Body != null)
            {
                foreach (var block in Body)
                {
                    if (block.Hidden)
                    {
                        continue;
                    }
                    if (block.Statements != null)
                    {
                        foreach (var stmt in block.Statements)
                        {
                            // Skip CatchExpression and jump statements
                            if (!IsControlFlowNode(stmt, includeCatch: true))
                            {
                                tryBlock.Statements.Add(NormalizeNode(stmt));
                            }
                        }
                    }
                }
            }
            tryStatement.Try = tryBlock;

            // Build catch block with both clause and body
            if (CatchClause != null)
            {
                var catchBlock = new BlockStatement();
                
                // First add a local variable declaration for the exception
                // This will be handled by the catch clause expression
                
                // Add catch body statements
                if (CatchBody != null && CatchBody.Count > 0)
                {
                    foreach (var block in CatchBody)
                    {
                        if (block.Hidden)
                        {
                            continue;
                        }
                        if (block.Statements != null)
                        {
                            foreach (var stmt in block.Statements)
                            {
                                // Skip jump statements at the end
                                if (!IsControlFlowNode(stmt, includeCatch: false))
                                {
                                    catchBlock.Statements.Add(NormalizeNode(stmt));
                                }
                            }
                        }
                    }
                }
                
                tryStatement.Catch = new ExpressionStatement(CatchClause);
                tryStatement.Finally = catchBlock; // Temporarily use Finally to hold catch body
            }

            HideBlocks();
            return tryStatement;
        }
    }
}
