using System.Collections.Generic;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class ForLogic : ILogical
    {
        public Expression Initializer { get; set; }
        public Expression Condition { get; set; }
        public Expression Increment { get; set; }
        public List<Block> Body { get; set; }

        private void HideBlocks()
        {
            Body.SafeHide();
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