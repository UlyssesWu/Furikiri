using System.Collections.Generic;
using Furikiri.AST.Expressions;

namespace Furikiri.AST.Statements
{
    class ForStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.ForStatement;
        public override IEnumerable<IAstNode> Children { get; }

        public Expression Initializer { get; set; }
        public Expression Condition { get; set; }
        public Expression Increment { get; set; }

        public ForStatement(Expression init, Expression cond, Expression inc)
        {
            Initializer = init;
            Condition = cond;
            Increment = inc;
        }
    }
}