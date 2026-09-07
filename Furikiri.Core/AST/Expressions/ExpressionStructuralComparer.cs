using System;
using System.Linq;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    /// <summary>
    /// 对表达式做保守的结构等价判断。
    /// </summary>
    /// <remarks>
    /// 调试用 ToString() 并不完整，例如成员标识符通常只返回成员名，因而
    /// left.value 与 right.value 都会变成 value。控制流化简若据此吸收条件，
    /// 会直接改变程序语义。本比较器只认可明确支持且全部语义字段一致的节点；
    /// 未知节点宁可判为不等价，让输出稍长，也不能误删求值路径。
    /// </remarks>
    internal static class ExpressionStructuralComparer
    {
        public static bool AreEquivalent(Expression left, Expression right)
        {
            left = UnwrapCondition(left);
            right = UnwrapCondition(right);

            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.GetType() != right.GetType())
            {
                return false;
            }

            return (left, right) switch
            {
                (LocalExpression l, LocalExpression r) =>
                    l.Slot == r.Slot && l.IsParameter == r.IsParameter,
                (IdentifierExpression l, IdentifierExpression r) =>
                    l.Name == r.Name &&
                    l.IdentifierType == r.IdentifierType &&
                    l.HideInstance == r.HideInstance &&
                    AreEquivalent(l.Instance, r.Instance),
                (ConstantExpression l, ConstantExpression r) =>
                    AreEquivalent(l.Variant, r.Variant),
                (UnaryExpression l, UnaryExpression r) =>
                    l.Op == r.Op &&
                    l.IsSelfAssignment == r.IsSelfAssignment &&
                    l.IsPrefix == r.IsPrefix &&
                    AreEquivalent(l.Target, r.Target),
                (BinaryExpression l, BinaryExpression r) =>
                    l.Op == r.Op &&
                    l.IsSelfAssignment == r.IsSelfAssignment &&
                    l.IsDeclaration == r.IsDeclaration &&
                    AreEquivalent(l.Left, r.Left) &&
                    AreEquivalent(l.Right, r.Right),
                (PropertyAccessExpression l, PropertyAccessExpression r) =>
                    AreEquivalent(l.Instance, r.Instance) &&
                    AreEquivalent(l.Property, r.Property),
                (InvokeExpression l, InvokeExpression r) => AreEquivalent(l, r),
                _ => false
            };
        }

        private static Expression UnwrapCondition(Expression expression)
        {
            while (expression is ConditionExpression condition)
            {
                expression = condition.Condition;
            }

            return expression;
        }

        private static bool AreEquivalent(InvokeExpression left, InvokeExpression right)
        {
            return left.InvokeType == right.InvokeType &&
                   left.MethodName == right.MethodName &&
                   (ReferenceEquals(left.MethodObject, right.MethodObject) ||
                    left.MethodObject == null && right.MethodObject == null) &&
                   AreEquivalent(left.Instance, right.Instance) &&
                   AreEquivalent(left.MethodExpression, right.MethodExpression) &&
                   left.HasOmittedArguments == right.HasOmittedArguments &&
                   HaveEquivalentSpreadParameters(left, right) &&
                   left.Parameters.Count == right.Parameters.Count &&
                   left.Parameters.Zip(right.Parameters, AreEquivalent).All(equal => equal);
        }

        private static bool HaveEquivalentSpreadParameters(
            InvokeExpression left, InvokeExpression right)
        {
            if (left.SpreadParameterIndices == null || left.SpreadParameterIndices.Count == 0)
            {
                return right.SpreadParameterIndices == null ||
                       right.SpreadParameterIndices.Count == 0;
            }

            return right.SpreadParameterIndices != null &&
                   left.SpreadParameterIndices.SetEquals(right.SpreadParameterIndices);
        }

        private static bool AreEquivalent(ITjsVariant left, ITjsVariant right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Type != right.Type ||
                left.GetType() != right.GetType())
            {
                return false;
            }

            return (left, right) switch
            {
                (TjsVoid, TjsVoid) => true,
                (TjsInt l, TjsInt r) => l.IntValue == r.IntValue,
                // 常量身份不能使用浮点相等：+0/-0 的倒数符号不同，必须保留分支。
                (TjsReal l, TjsReal r) => l.InternalType == r.InternalType &&
                    (l.InternalType == TjsInternalType.Long
                        ? l.LongValue == r.LongValue
                        : BitConverter.DoubleToInt64Bits(l.DoubleValue) ==
                          BitConverter.DoubleToInt64Bits(r.DoubleValue)),
                (TjsString l, TjsString r) => l.StringValue == r.StringValue,
                (TjsOctet l, TjsOctet r) =>
                    l.BytesValue.SequenceEqual(r.BytesValue),
                (TjsCodeObject l, TjsCodeObject r) =>
                    ReferenceEquals(l.Object, r.Object) && ReferenceEquals(l.This, r.This),
                (TjsObject l, TjsObject r) => Equals(l.Value, r.Value),
                _ => false
            };
        }
    }
}
