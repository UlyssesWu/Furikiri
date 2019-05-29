using System.Collections.Generic;
using Furikiri.AST.Expressions;

namespace Furikiri.AST.Statements
{
    class WhileStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.WhileStatement;
        public override IEnumerable<IAstNode> Children { get; }

        public Expression Condition { get; set; }
        public BlockStatement Body { get; set; }

        public WhileStatement(Expression condition, BlockStatement body)
        {
            Condition = condition;
            Body = body;
        }
    }
}