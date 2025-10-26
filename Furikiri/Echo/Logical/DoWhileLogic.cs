using System.Collections.Generic;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class DoWhileLogic : ILogical, IConditional
    {
        public Block Continue { get; set; }
        public Expression Condition { get; set; }
        public Block Break { get; set; }
        public List<Block> Body { get; set; }
        public bool IsWhile { get; set; }
        public List<Loop> AllLoops { get; set; }
        public Loop CurrentLoop { get; set; }

        public DoWhileLogic(bool isWhile = false)
        {
            IsWhile = isWhile;
        }

        private void HideBlocks()
        {
            // Hide all body blocks unconditionally to prevent duplicate collection
            // After if-else structuring, blocks may contain only a Statement, 
            // but they should still be hidden as they're part of the loop body
            foreach (var block in Body)
            {
                block.Hidden = true;
            }
        }

        public Statement ToStatement()
        {
            var body = new BlockStatement(Body, false);
            body.ResolveFromBlocks(AllLoops, CurrentLoop);
            if (IsWhile)
            {
                WhileStatement dw = new WhileStatement(Condition, body);
                HideBlocks();
                return dw;
            }
            else
            {
                DoWhileStatement dw = new DoWhileStatement(Condition, body);
                HideBlocks();
                return dw;
            }
        }
    }
}
