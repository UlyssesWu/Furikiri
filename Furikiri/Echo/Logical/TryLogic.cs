using System;
using System.Collections.Generic;
using System.Linq;
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
            EnterTry.Hidden = true;
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

            // Build try block
            var tryBlock = new BlockStatement();
            if (Body != null)
            {
                foreach (var block in Body)
                {
                    if (block.Statements != null)
                    {
                        foreach (var stmt in block.Statements)
                        {
                            // Skip CatchExpression and jump statements
                            if (!(stmt is CatchExpression) && !(stmt is GotoExpression) && !(stmt is IJump))
                            {
                                tryBlock.Statements.Add(stmt);
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
                        if (block.Statements != null)
                        {
                            foreach (var stmt in block.Statements)
                            {
                                // Skip jump statements at the end
                                if (!(stmt is GotoExpression) && !(stmt is IJump))
                                {
                                    catchBlock.Statements.Add(stmt);
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
