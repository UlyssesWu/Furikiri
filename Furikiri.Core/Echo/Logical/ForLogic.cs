using System.Collections.Generic;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class ForLogic : ILogical, IConditional
    {
        public Expression Initializer { get; set; }
        public Expression Condition { get; set; }
        public Expression Increment { get; set; }
        public List<Block> Body { get; set; }

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
            var body = new BlockStatement(Body, true);
            ForStatement f = new ForStatement(Initializer, Condition, Increment) {Body = body};
            HideBlocks();
            return f;
        }
    }
}