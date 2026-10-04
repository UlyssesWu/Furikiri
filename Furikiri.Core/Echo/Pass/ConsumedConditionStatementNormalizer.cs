using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 删除已经被相邻值表达式消费的原始条件节点。
    /// </summary>
    /// <remarks>
    /// SETF 会在合流点把一串短路条件恢复成 Phi 值。区域结构化后，最前面的
    /// ConditionExpression 偶尔会与该 Phi 赋值同时进入同一语句块，造成首次调用
    /// 既作为独立语句执行，又在短路表达式中执行。这里只处理紧邻赋值且右值确实
    /// 包含同一条件的形态，不按变量名、方法名或源文件做判断。
    /// </remarks>
    internal static class ConsumedConditionStatementNormalizer
    {
        public static void Normalize(BlockStatement block)
        {
            if (block?.Statements == null || block.Statements.Count < 2)
            {
                return;
            }

            for (var index = block.Statements.Count - 2; index >= 0; index--)
            {
                var condition = GetExpression(block.Statements[index]) as ConditionExpression;
                var assignment = GetExpression(block.Statements[index + 1]) as BinaryExpression;
                if (condition?.Condition == null ||
                    assignment?.Op != BinaryOp.Assign ||
                    assignment.Right == null ||
                    !ContainsEquivalent(assignment.Right, condition.Condition))
                {
                    continue;
                }

                block.Statements.RemoveAt(index);
            }
        }

        private static Expression GetExpression(IAstNode node)
        {
            return node switch
            {
                Expression expression => expression,
                ExpressionStatement statement => statement.Expression,
                _ => null
            };
        }

        internal static bool ContainsEquivalent(Expression expression, Expression target)
        {
            if (expression == null || target == null)
            {
                return false;
            }

            if (ExpressionStructuralComparer.AreEquivalent(expression, target))
            {
                return true;
            }

            if (expression is PhiExpression phi &&
                (ContainsEquivalent(phi.Condition?.Condition, target) ||
                 ContainsEquivalent(phi.ThenBranch, target) ||
                 ContainsEquivalent(phi.ElseBranch, target)))
            {
                return true;
            }

            foreach (var child in expression.Children ?? System.Array.Empty<IAstNode>())
            {
                if (child is Expression childExpression &&
                    ContainsEquivalent(childExpression, target))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
