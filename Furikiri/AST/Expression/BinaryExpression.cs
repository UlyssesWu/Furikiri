using System.Collections.Generic;
using Furikiri.Echo;
using Furikiri.Emit;

namespace Furikiri.AST.Expression
{
    /// <summary>
    /// Binary Expression
    /// </summary>
    class BinaryExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.BinaryExpression;
        public override List<IAstNode> Children { get; } = new List<IAstNode>();
        public BinaryOp Op { get; set; }

        public TjsVarType? ResultType { get; set; }

        public Expression Left
        {
            get => Children[0] as Expression;
            set => Children[0] = value;
        }

        public Expression Right
        {
            get => Children[1] as Expression;
            set => Children[1] = value;
        }

        public BinaryExpression(Expression left, Expression right, BinaryOp op)
        {
            Children = new List<IAstNode>(2) {left, right};
            Op = op;
        }
    }
}