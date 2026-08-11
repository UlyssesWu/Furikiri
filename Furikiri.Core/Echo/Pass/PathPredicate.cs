using System;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 表示从某个 CFG 决策节点最终到达指定分支体的条件。
    /// </summary>
    /// <remarks>
    /// 叶节点只有三种状态：恒真表示已经到达目标分支，恒假表示到达其他出口，
    /// Expression 表示仍需满足一段 TJS2 条件。控制流遍历负责生成叶节点，本类
    /// 只负责按真、假边组合布尔表达式，因此不依赖 Block 或 DecompileContext。
    /// </remarks>
    internal sealed class PathPredicate
    {
        public bool? Constant { get; private set; }
        public Expression Expression { get; private set; }

        public static PathPredicate True() => new PathPredicate { Constant = true };
        public static PathPredicate False() => new PathPredicate { Constant = false };
        public static PathPredicate From(Expression expression) =>
            new PathPredicate { Expression = expression };

        /// <summary>
        /// 将当前条件的真、假后继谓词合成一个等价的短路表达式。
        /// </summary>
        /// <remarks>
        /// 一般形式为 <c>(condition &amp;&amp; whenTrue) ||
        /// (!condition &amp;&amp; whenFalse)</c>。实现会优先应用恒等律、吸收律和
        /// 公共因子消除，既减少冗余条件，也保持可能带调用的表达式求值顺序。
        /// </remarks>
        public static PathPredicate Combine(
            Expression condition, PathPredicate whenTrue, PathPredicate whenFalse)
        {
            if (whenTrue.Constant == true && whenFalse.Constant == false)
            {
                return From(condition);
            }

            if (whenTrue.Constant == false && whenFalse.Constant == true)
            {
                return From(condition.Invert());
            }

            if (whenTrue.Constant == true && whenFalse.Expression != null)
            {
                return From(CombineBoolean(condition, whenFalse.Expression, BinaryOp.LogicOr));
            }

            if (whenTrue.Expression != null && whenFalse.Constant == true)
            {
                return From(CombineBoolean(condition.Invert(), whenTrue.Expression, BinaryOp.LogicOr));
            }

            if (whenTrue.Expression != null && whenFalse.Constant == false)
            {
                return From(CombineBoolean(condition, whenTrue.Expression, BinaryOp.LogicAnd));
            }

            if (whenTrue.Constant == false && whenFalse.Expression != null)
            {
                return From(CombineBoolean(condition.Invert(), whenFalse.Expression, BinaryOp.LogicAnd));
            }

            if (whenTrue.Constant == whenFalse.Constant && whenTrue.Constant != null)
            {
                return whenTrue.Constant == true ? True() : False();
            }

            if (whenTrue.Expression == null || whenFalse.Expression == null)
            {
                return null;
            }

            var trueExpression = whenTrue.Expression;
            var falseExpression = whenFalse.Expression;
            if (AreEquivalent(trueExpression, falseExpression))
            {
                return From(trueExpression);
            }

            // A ? (X || Y) : X  =>  (A && Y) || X。
            // 条件项必须在前，才能保持 X/Y 中函数调用的原始求值顺序。
            if (TryRemoveBooleanFactor(trueExpression, BinaryOp.LogicOr,
                    falseExpression, out var trueRemainder))
            {
                return From(CombineBoolean(
                    CombineBoolean(condition, trueRemainder, BinaryOp.LogicAnd),
                    falseExpression,
                    BinaryOp.LogicOr));
            }

            // A ? X : (X || Y)  =>  (!A && Y) || X。
            if (TryRemoveBooleanFactor(falseExpression, BinaryOp.LogicOr,
                    trueExpression, out var falseRemainder))
            {
                return From(CombineBoolean(
                    CombineBoolean(condition.Invert(), falseRemainder, BinaryOp.LogicAnd),
                    trueExpression,
                    BinaryOp.LogicOr));
            }

            // A ? (X && Y) : X  =>  (!A || Y) && X。
            if (TryRemoveBooleanFactor(trueExpression, BinaryOp.LogicAnd,
                    falseExpression, out trueRemainder))
            {
                return From(CombineBoolean(
                    CombineBoolean(condition.Invert(), trueRemainder, BinaryOp.LogicOr),
                    falseExpression,
                    BinaryOp.LogicAnd));
            }

            // A ? X : (X && Y)  =>  (A || Y) && X。
            if (TryRemoveBooleanFactor(falseExpression, BinaryOp.LogicAnd,
                    trueExpression, out falseRemainder))
            {
                return From(CombineBoolean(
                    CombineBoolean(condition, falseRemainder, BinaryOp.LogicOr),
                    trueExpression,
                    BinaryOp.LogicAnd));
            }

            return From(CombineBoolean(
                CombineBoolean(condition, trueExpression, BinaryOp.LogicAnd),
                CombineBoolean(condition.Invert(), falseExpression, BinaryOp.LogicAnd),
                BinaryOp.LogicOr));
        }

        private static Expression CombineBoolean(Expression left, Expression right, BinaryOp op)
        {
            if (AreEquivalent(left, right))
            {
                return left;
            }

            var absorbingOp = op == BinaryOp.LogicOr ? BinaryOp.LogicAnd : BinaryOp.LogicOr;
            var rightContainsLeft = TryRemoveBooleanFactor(right, absorbingOp, left, out _);
            var leftContainsRight = TryRemoveBooleanFactor(left, absorbingOp, right, out _);
            if (rightContainsLeft || leftContainsRight)
            {
                // X || (X && Y) = X；X && (X || Y) = X。
                return rightContainsLeft ? left : right;
            }

            return op == BinaryOp.LogicOr ? left.Or(right) : left.And(right);
        }

        /// <summary>
        /// 从同一逻辑运算树中删除一个公共因子，并返回剩余表达式。
        /// 递归处理结合律展开后的多项条件，例如 X || Y || Z。
        /// </summary>
        private static bool TryRemoveBooleanFactor(
            Expression expression, BinaryOp op, Expression factor, out Expression remainder)
        {
            remainder = null;
            if (expression is not BinaryExpression binary || binary.Op != op)
            {
                return false;
            }

            if (AreEquivalent(binary.Left, factor))
            {
                remainder = binary.Right;
                return true;
            }

            if (AreEquivalent(binary.Right, factor))
            {
                remainder = binary.Left;
                return true;
            }

            if (TryRemoveBooleanFactor(binary.Left, op, factor, out var leftRemainder))
            {
                remainder = CombineBoolean(leftRemainder, binary.Right, op);
                return true;
            }

            if (TryRemoveBooleanFactor(binary.Right, op, factor, out var rightRemainder))
            {
                remainder = CombineBoolean(binary.Left, rightRemainder, op);
                return true;
            }

            return false;
        }

        private static bool AreEquivalent(Expression left, Expression right)
        {
            return ReferenceEquals(left, right) ||
                   left != null && right != null && left.GetType() == right.GetType() &&
                   string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
        }
    }
}
