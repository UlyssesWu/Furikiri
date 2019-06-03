using System.Collections.Generic;
using Furikiri.AST.Expressions;
using Furikiri.Echo;

namespace Furikiri.AST
{
    internal static class AstExtensions
    {
        public static bool IsCondition(this List<IAstNode> statements)
        {
            return statements.Count == 1 && statements[0] is ConditionExpression;
        }

        public static UnaryExpression Invert(this Expression exp)
        {
            return new UnaryExpression(exp, UnaryOp.Not);
        }

        public static BinaryExpression Or(this Expression left, Expression right)
        {
            return new BinaryExpression(left, right, BinaryOp.LogicOr);
        }

        public static BinaryExpression And(this Expression left, Expression right)
        {
            return new BinaryExpression(left, right, BinaryOp.LogicAnd);
        }
    }
}
