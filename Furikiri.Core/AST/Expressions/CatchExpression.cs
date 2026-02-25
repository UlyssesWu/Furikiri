using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    internal class CatchExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.CatchClause;
        public override IEnumerable<IAstNode> Children { get; }

        public Expression Exception { get; set; }

        public CatchExpression(Expression exception = null)
        {
            Exception = exception;
        }
    }
}
