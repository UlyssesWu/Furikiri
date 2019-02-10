using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Echo;
using Furikiri.Emit;

namespace Furikiri.AST.Expression
{
    class UnaryExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.UnaryExpression;
        public override List<IAstNode> Children { get; }
        public UnaryOp Op { get; set; }
        public TjsVarType? ResultType { get; set; }

        public Expression Target
        {
            get => (Expression) Children[0];
            set => Children[0] = value;
        }

        public UnaryExpression(Expression target, UnaryOp op)
        {
            Op = op;
            Children = new List<IAstNode>(1) {target};
        }
    }
}