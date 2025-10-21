using System.Collections.Generic;
using System.Linq;

// ReSharper disable CommentTypo

namespace Furikiri.AST.Expressions
{
    /// <summary>
    /// Expression for undetermined variable (SSA Phi node)
    /// </summary>
    /// <remarks>
    /// Phi nodes are used in SSA form to represent variables that have different values
    /// coming from different control flow paths. They need to be resolved during decompilation
    /// to produce readable code.
    /// </remarks>
    /// Elapsam semel occasionem non ipse potest Iuppiter reprehendere https://zh.moegirl.org/Phi
    class PhiExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.PhiExpression;
        public override IEnumerable<IAstNode> Children => PossibleExpressions;

        /// <summary>
        /// Register slot this Phi node represents
        /// </summary>
        public int Slot { get; set; }

        /// <summary>
        /// Possible expressions from different control flow paths
        /// </summary>
        public List<Expression> PossibleExpressions { get; set; } = new List<Expression>();

        /// <summary>
        /// Condition expression if this is from an if-else merge point
        /// </summary>
        public ConditionExpression Condition { get; set; }

        /// <summary>
        /// Expression from the true branch
        /// </summary>
        public Expression ThenBranch { get; set; }

        /// <summary>
        /// Expression from the false branch
        /// </summary>
        public Expression ElseBranch { get; set; }

        /// <summary>
        /// Indicates if this Phi can be simplified
        /// </summary>
        public bool CanSimplify => PossibleExpressions.Count > 0 && 
                                     PossibleExpressions.All(e => e != null && AreSemanticallyEqual(e, PossibleExpressions[0]));

        /// <summary>
        /// Indicates if this Phi represents a conditional expression (ternary)
        /// </summary>
        public bool IsConditional => Condition != null && ThenBranch != null && ElseBranch != null;

        public PhiExpression(int slot)
        {
            Slot = slot;
        }

        /// <summary>
        /// Simplify Phi node if all branches have the same value
        /// </summary>
        public Expression Simplify()
        {
            if (CanSimplify)
            {
                return PossibleExpressions[0];
            }
            return this;
        }

        /// <summary>
        /// Check if two expressions are semantically equal
        /// </summary>
        private bool AreSemanticallyEqual(Expression a, Expression b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.GetType() != b.GetType()) return false;

            // LocalExpression: compare by slot
            if (a is LocalExpression la && b is LocalExpression lb)
            {
                return la.Slot == lb.Slot;
            }

            // ConstantExpression: compare by variant
            if (a is ConstantExpression ca && b is ConstantExpression cb)
            {
                return ca.Variant?.Equals(cb.Variant) ?? cb.Variant == null;
            }

            // BinaryExpression: deep comparison
            if (a is BinaryExpression ba && b is BinaryExpression bb)
            {
                return ba.Op == bb.Op && 
                       AreSemanticallyEqual(ba.Left, bb.Left) && 
                       AreSemanticallyEqual(ba.Right, bb.Right);
            }

            // UnaryExpression: deep comparison
            if (a is UnaryExpression ua && b is UnaryExpression ub)
            {
                return ua.Op == ub.Op && AreSemanticallyEqual(ua.Target, ub.Target);
            }

            // IdentifierExpression: compare by name
            if (a is IdentifierExpression ia && b is IdentifierExpression ib)
            {
                return ia.FullName == ib.FullName;
            }

            // For other types, use ToString comparison (not perfect but practical)
            return a.ToString() == b.ToString();
        }

        public override string ToString()
        {
            if (IsConditional)
            {
                return $"({Condition.Condition} ? {ThenBranch} : {ElseBranch})";
            }
            
            if (PossibleExpressions.Count > 0)
            {
                return $"phi({string.Join(", ", PossibleExpressions)})";
            }
            
            return $"phi<slot:{Slot}>";
        }
    }
}