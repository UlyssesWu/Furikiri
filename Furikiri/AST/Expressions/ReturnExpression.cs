using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    class ReturnExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.ReturnExpression;
        public override IEnumerable<IAstNode> Children { get; }

        public Expression Return { get; set; }

        public ReturnExpression(Expression ret = null)
        {
            Return = ret;
        }
    }
}