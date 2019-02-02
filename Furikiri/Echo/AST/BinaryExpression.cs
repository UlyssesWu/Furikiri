using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.AST
{
    class BinaryExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.BinaryExpresssion;
        public override List<IAstNode> Children { get; } = new List<IAstNode>();
        public BinaryOp BinaryOp { get; set; }

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
            Children = new List<IAstNode>(2) {null, null};
        }
    }
}