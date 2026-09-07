using System.Collections.Generic;
using Furikiri;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo
{
    /// <summary>
    /// 统一描述表达式是否可能修改状态或触发可观察求值。
    /// </summary>
    /// <remarks>
    /// 控制流恢复和语言输出必须使用同一套结论；否则新增运算可能在前一阶段
    /// 被保留，却在后一阶段又被当成空壳删除。属性读取在兼容查询中沿用现有
    /// AST 约定；需要严格证明可重复求值时则保守拒绝。
    /// </remarks>
    internal static class ExpressionEffectAnalysis
    {
        /// <summary>
        /// 判断表达式树中是否含调用、写入、删除或显式控制转移。
        /// </summary>
        public static bool HasObservableEffect(Expression expression)
        {
            if (expression?.RequiresStandaloneEvaluation == true) return true;
            return expression switch
            {
                null => false,
                InvokeExpression => true,
                ReturnExpression => true,
                ThrowExpression => true,
                DeleteExpression => true,
                BinaryExpression binary when IsPotentialMutation(binary) => true,
                BinaryExpression binary =>
                    HasObservableEffect(binary.Left) ||
                    HasObservableEffect(binary.Right),
                UnaryExpression unary when IsMutation(unary) => true,
                UnaryExpression unary => HasObservableEffect(unary.Target),
                ConditionExpression condition =>
                    HasObservableEffect(condition.Condition),
                PropertyAccessExpression property =>
                    HasObservableEffect(property.Instance) ||
                    HasObservableEffect(property.Property),
                _ => false
            };
        }

        /// <summary>
        /// 判断控制流入口语句是否含局部初始化之外的可观察求值。
        /// 写入 VM 局部槽本身只是在恢复值状态；右值中的调用、成员写入等仍必须
        /// 保留。非局部赋值则按普通可观察效果处理。
        /// </summary>
        public static bool HasObservableEffectBeyondLocalInitialization(
            Expression expression)
        {
            return expression is BinaryExpression
                { Op: BinaryOp.Assign, Left: LocalExpression } assignment
                ? HasObservableEffect(assignment.Right)
                : HasObservableEffect(expression);
        }

        /// <summary>
        /// 判断表达式是否适合在等价改写时重复或移动。属性访问沿用现有 AST
        /// 约定，仅检查其对象与键；调用和所有修改型表达式均拒绝。
        /// </summary>
        public static bool IsLikelyPure(Expression expression)
        {
            if (expression?.RequiresStandaloneEvaluation == true) return false;
            return expression switch
            {
                null => true,
                ConstantExpression => true,
                IdentifierExpression => true,
                LocalExpression => true,
                UnaryExpression unary when IsMutation(unary) => false,
                UnaryExpression unary => IsLikelyPure(unary.Target),
                BinaryExpression binary when IsExplicitMutation(binary) => false,
                BinaryExpression binary =>
                    IsLikelyPure(binary.Left) && IsLikelyPure(binary.Right),
                ConditionExpression condition =>
                    IsLikelyPure(condition.Condition),
                PropertyAccessExpression property =>
                    IsLikelyPure(property.Instance) &&
                    IsLikelyPure(property.Property),
                _ => false
            };
        }

        /// <summary>
        /// 严格证明表达式无副作用。只接受无需调用用户代码的基础运算；属性
        /// 访问和未知节点均保守拒绝。
        /// </summary>
        public static bool IsProvablySideEffectFree(Expression expression)
        {
            if (expression?.RequiresStandaloneEvaluation == true) return false;
            if (expression == null)
            {
                return true;
            }

            return expression switch
            {
                ConstantExpression => true,
                IdentifierExpression => true,
                LocalExpression => true,
                UnaryExpression unary when unary.Op is UnaryOp.Not or
                    UnaryOp.InvertSign or UnaryOp.ToInt or UnaryOp.ToReal or
                    UnaryOp.ToString or UnaryOp.ToNumber or UnaryOp.ToByteArray or
                    UnaryOp.ToCharacterCode or UnaryOp.FromCharacterCode =>
                    IsProvablySideEffectFree(unary.Target),
                BinaryExpression binary when !IsExplicitMutation(binary) =>
                    IsProvablySideEffectFree(binary.Left) &&
                    IsProvablySideEffectFree(binary.Right),
                ConditionExpression condition =>
                    IsProvablySideEffectFree(condition.Condition),
                _ => false
            };
        }

        /// <summary>
        /// 按求值树收集最外层可观察表达式。赋值或调用作为整体保留，避免再
        /// 单独输出其内部调用而改变求值次数。
        /// </summary>
        public static void CollectObservableEffects(
            Expression expression, ICollection<Expression> effects)
        {
            if (expression == null || effects == null)
            {
                return;
            }

            switch (expression)
            {
                case InvokeExpression:
                case ReturnExpression:
                case ThrowExpression:
                case DeleteExpression:
                case IdentifierExpression { Instance: not null }:
                case PropertyAccessExpression:
                    effects.Add(expression);
                    return;
                case BinaryExpression binary when IsExplicitMutation(binary):
                    effects.Add(binary);
                    return;
                case UnaryExpression unary when IsMutation(unary):
                    effects.Add(unary);
                    return;
                case BinaryExpression binary:
                    CollectObservableEffects(binary.Left, effects);
                    CollectObservableEffects(binary.Right, effects);
                    return;
                case UnaryExpression unary:
                    CollectObservableEffects(unary.Target, effects);
                    return;
                case ConditionExpression condition:
                    CollectObservableEffects(condition.Condition, effects);
                    return;
            }
        }

        private static bool IsPotentialMutation(BinaryExpression binary)
        {
            return binary != null &&
                   (binary.IsSelfAssignment ||
                    binary.Op is BinaryOp.Assign or BinaryOp.Swap ||
                    // 某些旧节点在写出前才补 IsSelfAssignment。分析阶段对可写
                    // 运算符保持保守，防止整个载荷分支被误删。
                    binary.Op.CanSelfAssign());
        }

        private static bool IsExplicitMutation(BinaryExpression binary)
        {
            return binary != null &&
                   (binary.IsSelfAssignment ||
                    binary.Op is BinaryOp.Assign or BinaryOp.Swap);
        }

        private static bool IsMutation(UnaryExpression unary)
        {
            return unary?.Op is UnaryOp.Inc or UnaryOp.Dec or
                UnaryOp.Invalidate or UnaryOp.Eval;
        }
    }
}
