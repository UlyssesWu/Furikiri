using Furikiri.AST;
using Furikiri.AST.Expressions;
using System.Collections.Generic;

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
            if (ExpressionStructuralComparer.AreEquivalent(trueExpression, falseExpression))
            {
                return From(trueExpression);
            }

            // A ? (X op C) : (Y op C) => (A ? X : Y) op C。
            // 只提取两个分支最右侧的公共项：分支判定、X/Y、C 的求值顺序
            // 与原字节码一致；任意位置的交换律提取可能移动带调用的表达式。
            if (TryCombineWithCommonTrailingFactor(
                    condition, trueExpression, falseExpression,
                    BinaryOp.LogicOr, out var commonFactored) ||
                TryCombineWithCommonTrailingFactor(
                    condition, trueExpression, falseExpression,
                    BinaryOp.LogicAnd, out commonFactored))
            {
                return From(commonFactored);
            }

            // A ? X : (B && X) || Y
            //   => ((A || B) && X) || (!A && Y)。
            // 这是短路条件编译后常见的 Shannon 展开。要求 A 稳定、B 位于 X
            // 之前且整项位于 Y 之前，从而不会改变调用的先后或执行次数。
            if (TryFactorGuardedAlternative(
                    condition, trueExpression, falseExpression, out var guardedFactored) ||
                TryFactorGuardedAlternative(
                    condition.Invert(), falseExpression, trueExpression, out guardedFactored))
            {
                return From(guardedFactored);
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

        private static bool TryCombineWithCommonTrailingFactor(
            Expression condition,
            Expression trueExpression,
            Expression falseExpression,
            BinaryOp op,
            out Expression result)
        {
            result = null;
            if (!TrySplitTrailingFactor(trueExpression, op,
                    out var trueCore, out var trueTail) ||
                !TrySplitTrailingFactor(falseExpression, op,
                    out var falseCore, out var falseTail) ||
                !ExpressionStructuralComparer.AreEquivalent(trueTail, falseTail))
            {
                return false;
            }

            var combinedCore = Combine(condition, From(trueCore), From(falseCore));
            if (combinedCore?.Expression == null)
            {
                return false;
            }

            result = CombineBoolean(combinedCore.Expression, trueTail, op);
            return true;
        }

        private static bool TryFactorGuardedAlternative(
            Expression commonCondition,
            Expression commonExpression,
            Expression alternative,
            out Expression result)
        {
            result = null;
            if (!IsStableAssumption(commonCondition))
            {
                return false;
            }

            var terms = new List<Expression>();
            CollectBooleanTerms(alternative, BinaryOp.LogicOr, terms);
            if (terms.Count == 0 ||
                !TrySplitTrailingFactor(terms[0], BinaryOp.LogicAnd,
                    out var guard, out var guardedExpression) ||
                !ExpressionStructuralComparer.AreEquivalent(
                    guardedExpression, commonExpression))
            {
                return false;
            }

            var guardedCommon = CombineBoolean(
                CombineBoolean(commonCondition, guard, BinaryOp.LogicOr),
                commonExpression,
                BinaryOp.LogicAnd);

            if (terms.Count == 1)
            {
                result = guardedCommon;
                return true;
            }

            var remainder = RebuildBooleanTerms(terms, 1, BinaryOp.LogicOr);
            result = CombineBoolean(
                guardedCommon,
                CombineBoolean(commonCondition.Invert(), remainder, BinaryOp.LogicAnd),
                BinaryOp.LogicOr);
            return true;
        }

        private static bool TrySplitTrailingFactor(
            Expression expression, BinaryOp op, out Expression core, out Expression factor)
        {
            core = null;
            factor = null;
            if (expression is not BinaryExpression binary || binary.Op != op)
            {
                return false;
            }

            core = binary.Left;
            factor = binary.Right;
            return true;
        }

        private static void CollectBooleanTerms(
            Expression expression, BinaryOp op, ICollection<Expression> terms)
        {
            if (expression is BinaryExpression binary && binary.Op == op)
            {
                CollectBooleanTerms(binary.Left, op, terms);
                CollectBooleanTerms(binary.Right, op, terms);
                return;
            }

            terms.Add(expression);
        }

        private static Expression RebuildBooleanTerms(
            IReadOnlyList<Expression> terms, int start, BinaryOp op)
        {
            var result = terms[start];
            for (var i = start + 1; i < terms.Count; i++)
            {
                result = CombineBoolean(result, terms[i], op);
            }

            return result;
        }

        private static Expression CombineBoolean(Expression left, Expression right, BinaryOp op)
        {
            if (ExpressionStructuralComparer.AreEquivalent(left, right))
            {
                return left;
            }

            // `A && (...)` 的右侧只会在 A 为真时求值，`A || (...)` 则只会在
            // A 为假时求值。路径合并容易把已经确定的 A/!A 再写进右侧分支，
            // 形成 `A && (B || !A && C)` 一类冗余式。仅对不含调用、赋值的
            // 稳定条件代入已知真值，既缩短谓词，也不改变副作用求值次数。
            if (IsStableAssumption(left))
            {
                var simplifiedRight = SimplifyUnderAssumption(
                    right, left, op == BinaryOp.LogicAnd);
                if (simplifiedRight.Constant == (op == BinaryOp.LogicAnd))
                {
                    return left;
                }

                if (simplifiedRight.Expression != null)
                {
                    right = simplifiedRight.Expression;
                    if (ExpressionStructuralComparer.AreEquivalent(left, right))
                    {
                        return left;
                    }
                }
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
        /// 在已知某个稳定条件真/假的前提下折叠逻辑树。返回常量时不直接生成
        /// TJS 字面量，而由调用方结合外层短路运算应用恒等律。
        /// </summary>
        private static PathPredicate SimplifyUnderAssumption(
            Expression expression, Expression assumption, bool assumptionValue)
        {
            if (ExpressionStructuralComparer.AreEquivalent(expression, assumption))
            {
                return assumptionValue ? True() : False();
            }

            var inverted = assumption.Invert();
            if (ExpressionStructuralComparer.AreEquivalent(expression, inverted))
            {
                return assumptionValue ? False() : True();
            }

            if (expression is not BinaryExpression binary ||
                binary.Op is not (BinaryOp.LogicAnd or BinaryOp.LogicOr))
            {
                return From(expression);
            }

            var left = SimplifyUnderAssumption(
                binary.Left, assumption, assumptionValue);
            var right = SimplifyUnderAssumption(
                binary.Right, assumption, assumptionValue);
            if (binary.Op == BinaryOp.LogicAnd)
            {
                if (left.Constant == false || right.Constant == false) return False();
                if (left.Constant == true) return right;
                if (right.Constant == true) return left;
            }
            else
            {
                if (left.Constant == true || right.Constant == true) return True();
                if (left.Constant == false) return right;
                if (right.Constant == false) return left;
            }

            if (left.Expression == null || right.Expression == null)
            {
                return From(expression);
            }

            return From(binary.Op == BinaryOp.LogicAnd
                ? left.Expression.And(right.Expression)
                : left.Expression.Or(right.Expression));
        }

        private static bool IsStableAssumption(Expression expression)
        {
            return expression switch
            {
                ConstantExpression or LocalExpression or IdentifierExpression => true,
                UnaryExpression unary when unary.Op is not (UnaryOp.Inc or UnaryOp.Dec or
                    UnaryOp.Invalidate or UnaryOp.Eval) => IsStableAssumption(unary.Target),
                BinaryExpression binary when binary.Op is not BinaryOp.Assign &&
                                             !binary.IsSelfAssignment &&
                                             !binary.Op.CanSelfAssign() =>
                    IsStableAssumption(binary.Left) && IsStableAssumption(binary.Right),
                _ => false
            };
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

            if (ExpressionStructuralComparer.AreEquivalent(binary.Left, factor))
            {
                remainder = binary.Right;
                return true;
            }

            if (ExpressionStructuralComparer.AreEquivalent(binary.Right, factor))
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

    }
}
