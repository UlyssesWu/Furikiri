using System.Collections.Generic;
using Furikiri.Echo;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    /// <summary>
    /// Binary Expression
    /// </summary>
    class BinaryExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.BinaryExpression;

        public override IEnumerable<IAstNode> Children
        {
            get
            {
                yield return Left;
                yield return Right;
            }
        }

        public BinaryOp Op { get; set; }

        public TjsVarType? ResultType { get; set; }

        public Expression Left { get; set; }

        public Expression Right { get; set; }

        public BinaryExpression(Expression left, Expression right, BinaryOp op)
        {
            Op = op;
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
    }
}