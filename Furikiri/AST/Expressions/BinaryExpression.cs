using System.Collections.Generic;
using Furikiri.Echo;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    /// <summary>
    /// Binary Expression
    /// </summary>
    class BinaryExpression : Expression, IOperation
    {
        public override AstNodeType Type => AstNodeType.BinaryExpression;

        private string DebugString => $"{Left}, {Right}, {Op.ToSymbol()}";

        public override IEnumerable<IAstNode> Children
        {
            get
            {
                yield return Left;
                yield return Right;
            }
        }
        public bool IsSelfAssignment { get; set; } = false;

        public bool IsDeclaration { get; set; }

        public BinaryOp Op { get; set; }

        public TjsVarType? ResultType { get; set; }

        public Expression Left { get; set; }

        public Expression Right { get; set; }

        public BinaryExpression(Expression left, Expression right, BinaryOp op)
        {
            Left = left;
            Right = right;
            Op = op;
            Left.Parent = this;
            Right.Parent = this;
        }

        public bool IsCompare
        {
            get
            {
                switch (Op)
                {
                    case BinaryOp.GreaterThan:
                    case BinaryOp.LessThan:
                    case BinaryOp.Equal:
                    case BinaryOp.Congruent:
                    case BinaryOp.NotEqual:
                    case BinaryOp.NotCongruent:
                        return true;
                    default:
                        return false;
                }
            }
        }

        public override string ToString()
        {
            return DebugString;
        }

    }
}