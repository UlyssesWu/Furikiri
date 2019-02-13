using System.Collections.Generic;
using Furikiri.AST.Expressions;

namespace Furikiri.AST.Statements
{
    class DoWhileStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.DoWhileStatement;
        public override IEnumerable<IAstNode> Children { get; }

        public BlockStatement Continue { get; set; }
        public Expression Condition { get; set; }
        public BlockStatement Break { get; set; }
        public BlockStatement Body { get; set; }

        public DoWhileStatement()
        {
        }
    }
}