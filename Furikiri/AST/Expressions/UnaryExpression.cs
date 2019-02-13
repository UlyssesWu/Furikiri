using System.Collections.Generic;
using Furikiri.Echo;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    class UnaryExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.UnaryExpression;

        public override IEnumerable<IAstNode> Children
        {
            get { yield return Target; }
        }

        public UnaryOp Op { get; set; }
        public TjsVarType? ResultType { get; set; }

        public Expression Target { get; set; }

        public UnaryExpression(Expression target, UnaryOp op)
        {
            Op = op;
        }
    }
}