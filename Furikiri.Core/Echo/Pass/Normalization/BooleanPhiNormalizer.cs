using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 对结构化 AST 中表示 VM 布尔标志的条件 Phi 做保守化简。
    /// </summary>
    /// <remarks>
    /// 这里只应用不复制条件、守卫或公共尾项的恒等式。普通值 Phi 不参与，
    /// 也不把任意三元式展开为 Shannon 形式，以免改变动态值和调用求值次数。
    /// </remarks>
    internal static class BooleanPhiNormalizer
    {
        public static void Normalize(BlockStatement block)
        {
            NormalizeBlock(block);
        }

        private static void NormalizeBlock(BlockStatement block)
        {
            if (block?.Statements == null)
            {
                return;
            }

            for (var index = 0; index < block.Statements.Count; index++)
            {
                block.Statements[index] = NormalizeNode(block.Statements[index]);
            }
        }

        private static IAstNode NormalizeNode(IAstNode node)
        {
            switch (node)
            {
                case Expression expression:
                    return NormalizeExpression(expression);
                case ExpressionStatement statement:
                    statement.Expression = NormalizeExpression(statement.Expression);
                    return statement;
                case BlockStatement block:
                    NormalizeBlock(block);
                    return block;
                case IfStatement conditional:
                    conditional.Condition = NormalizeExpression(conditional.Condition);
                    conditional.Then = NormalizeStatement(conditional.Then);
                    conditional.Else = NormalizeStatement(conditional.Else);
                    return conditional;
                case WhileStatement loop:
                    loop.Condition = NormalizeExpression(loop.Condition);
                    NormalizeBlock(loop.Body);
                    return loop;
                case DoWhileStatement loop:
                    loop.Condition = NormalizeExpression(loop.Condition);
                    NormalizeBlock(loop.Body);
                    return loop;
                case ForStatement loop:
                    loop.Initializer = NormalizeExpression(loop.Initializer);
                    loop.Condition = NormalizeExpression(loop.Condition);
                    loop.Increment = NormalizeExpression(loop.Increment);
                    NormalizeBlock(loop.Body);
                    return loop;
                case TryStatement tryStatement:
                    NormalizeBlock(tryStatement.Try);
                    if (tryStatement.Catch != null)
                    {
                        tryStatement.Catch.Expression =
                            NormalizeExpression(tryStatement.Catch.Expression);
                    }
                    NormalizeBlock(tryStatement.Finally);
                    return tryStatement;
                default:
                    return node;
            }
        }

        private static Statement NormalizeStatement(Statement statement)
        {
            return statement == null
                ? null
                : NormalizeNode(statement) as Statement ?? statement;
        }

        private static Expression NormalizeExpression(Expression expression)
        {
            switch (expression)
            {
                case null:
                    return null;
                case PhiExpression phi:
                    return NormalizePhi(phi);
                case BinaryExpression binary:
                    binary.Left = NormalizeExpression(binary.Left);
                    binary.Right = NormalizeExpression(binary.Right);
                    return binary;
                case UnaryExpression unary:
                    unary.Target = NormalizeExpression(unary.Target);
                    if (unary.Op == UnaryOp.Not &&
                        unary.Target is BinaryExpression invertible &&
                        IsDirectlyInvertible(invertible.Op))
                    {
                        // 比较取反和 De Morgan 变换都保持左到右短路顺序，且不
                        // 复制任何子表达式；在 AST 层完成可避免输出 `!(...)`
                        // 掩盖原本清晰的存在性守卫。
                        return invertible.Invert();
                    }
                    return unary;
                case ConditionExpression condition:
                    condition.Condition = NormalizeExpression(condition.Condition);
                    return condition;
                case ReturnExpression ret:
                    ret.Return = NormalizeExpression(ret.Return);
                    return ret;
                case ThrowExpression thrown:
                    thrown.Target = NormalizeExpression(thrown.Target);
                    return thrown;
                case DeleteExpression delete:
                    delete.Instance = NormalizeExpression(delete.Instance);
                    delete.IdentifierExpression =
                        NormalizeExpression(delete.IdentifierExpression);
                    return delete;
                case IdentifierExpression identifier:
                    identifier.Instance = NormalizeExpression(identifier.Instance);
                    return identifier;
                case PropertyAccessExpression property:
                    property.Instance = NormalizeExpression(property.Instance);
                    property.Property = NormalizeExpression(property.Property);
                    return property;
                case InvokeExpression invocation:
                    invocation.Instance = NormalizeExpression(invocation.Instance);
                    invocation.MethodExpression =
                        NormalizeExpression(invocation.MethodExpression);
                    for (var index = 0; index < invocation.Parameters.Count; index++)
                    {
                        invocation.Parameters[index] =
                            NormalizeExpression(invocation.Parameters[index]);
                    }
                    return invocation;
                default:
                    return expression;
            }
        }

        private static Expression NormalizePhi(PhiExpression phi)
        {
            if (phi.Condition != null)
            {
                phi.Condition.Condition =
                    NormalizeExpression(phi.Condition.Condition);
            }
            phi.ThenBranch = NormalizeExpression(phi.ThenBranch);
            phi.ElseBranch = NormalizeExpression(phi.ElseBranch);
            for (var index = 0; index < phi.PossibleExpressions.Count; index++)
            {
                phi.PossibleExpressions[index] =
                    NormalizeExpression(phi.PossibleExpressions[index]);
            }

            if (!phi.IsConditional)
            {
                return phi;
            }

            var condition = phi.Condition.Condition;
            var whenTrue = phi.ThenBranch;
            var whenFalse = phi.ElseBranch;
            if (ExpressionStructuralComparer.AreEquivalent(whenTrue, whenFalse))
            {
                return whenTrue;
            }

            // C ? (B ? X : Y) : Y => (C && B) ? X : Y
            // 这是普通值 Phi 也成立的控制流恒等式。它把短路守卫留在条件中，
            // 避免输出多层三元，同时不会复制 B、X、Y 中的任何求值。
            if (whenTrue is PhiExpression
                {
                    IsConditional: true
                } nestedTrue &&
                ExpressionStructuralComparer.AreEquivalent(
                    nestedTrue.ElseBranch, whenFalse))
            {
                var factored = new PhiExpression(phi.Slot)
                {
                    Condition = new ConditionExpression(
                        condition.And(nestedTrue.Condition.Condition), false),
                    ThenBranch = nestedTrue.ThenBranch,
                    ElseBranch = whenFalse
                };
                factored.PossibleExpressions.Add(factored.ThenBranch);
                factored.PossibleExpressions.Add(factored.ElseBranch);
                return NormalizePhi(factored);
            }

            if (phi.Slot != Const.FlagReg)
            {
                return phi;
            }

            // C ? C : X  => C || X
            if (ExpressionStructuralComparer.AreEquivalent(whenTrue, condition))
            {
                return condition.Or(whenFalse);
            }

            // C ? X : C  => C && X
            if (ExpressionStructuralComparer.AreEquivalent(whenFalse, condition))
            {
                return condition.And(whenTrue);
            }

            // C ? X : (B && X) => (C || B) && X
            if (TrySplitGuardedTail(whenFalse, whenTrue, out var falseGuard))
            {
                return condition.Or(falseGuard).And(whenTrue);
            }

            // C ? (B && X) : X => (!C || B) && X
            if (TrySplitGuardedTail(whenTrue, whenFalse, out var trueGuard))
            {
                return condition.Invert().Or(trueGuard).And(whenFalse);
            }

            // C ? (B || X) : X => (C && B) || X
            // 短路链的多个成功入口会把公共回退值 X 放在最右侧。递归剥离
            // 这个尾项可以保留外层守卫，同时仍只求值一次 X。
            if (TrySplitDisjunctionTail(whenTrue, whenFalse, out var truePrefix))
            {
                return condition.And(truePrefix).Or(whenFalse);
            }

            return phi;
        }

        private static bool TrySplitDisjunctionTail(
            Expression expression, Expression tail, out Expression prefix)
        {
            prefix = null;
            if (expression is not BinaryExpression
                {
                    Op: BinaryOp.LogicOr
                } disjunction)
            {
                return false;
            }

            if (ExpressionStructuralComparer.AreEquivalent(disjunction.Right, tail))
            {
                prefix = disjunction.Left;
                return true;
            }

            if (!TrySplitDisjunctionTail(disjunction.Right, tail, out var rightPrefix))
            {
                return false;
            }

            prefix = disjunction.Left.Or(rightPrefix);
            return true;
        }

        private static bool TrySplitGuardedTail(
            Expression expression, Expression tail, out Expression guard)
        {
            guard = null;
            if (expression is not BinaryExpression
                {
                    Op: BinaryOp.LogicAnd
                } conjunction ||
                !ExpressionStructuralComparer.AreEquivalent(
                    conjunction.Right, tail))
            {
                return false;
            }

            guard = conjunction.Left;
            return true;
        }

        private static bool IsDirectlyInvertible(BinaryOp op)
        {
            return op is BinaryOp.Equal or BinaryOp.NotEqual or
                BinaryOp.Congruent or BinaryOp.NotCongruent or
                BinaryOp.LessThan or BinaryOp.LessOrEqual or
                BinaryOp.GreaterThan or BinaryOp.GreaterOrEqual or
                BinaryOp.LogicAnd or BinaryOp.LogicOr;
        }
    }
}
