using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 将短路求值过程中被结构化成独立 if 的局部赋值放回原布尔表达式。
    /// </summary>
    /// <remarks>
    /// VM 常把 <c>C &amp;&amp; (v = call()) !== void &amp;&amp; use(v)</c>
    /// 拆成多个基本块。若同时保留 <c>if (C) v = call()</c> 和由标志 Phi
    /// 恢复出的 <c>C &amp;&amp; v !== void</c>，C 会被重复求值。这里只接受相邻、
    /// 同条件、单一局部赋值，且 v 是 C 后第一个求值项左操作数的形态；这样
    /// 把赋值移回去不会跨越任何其他求值，也不会改变调用次数。
    /// </remarks>
    internal static class GuardedAssignmentNormalizer
    {
        public static void Normalize(BlockStatement block)
        {
            if (block?.Statements == null || block.Statements.Count < 2)
            {
                return;
            }

            for (var index = 0; index < block.Statements.Count - 1; index++)
            {
                if (!TryGetGuardedLocalAssignment(
                        block.Statements[index], out var guard, out var assignment) ||
                    !TryGetAssignment(block.Statements[index + 1], out var consumer) ||
                    consumer.Right == null)
                {
                    continue;
                }

                var terms = new List<Expression>();
                CollectConjunctionTerms(consumer.Right, terms);
                if (terms.Count < 2 ||
                    !ExpressionStructuralComparer.AreEquivalent(terms[0], guard.Condition) ||
                    terms[1] is not BinaryExpression firstUse ||
                    assignment.Left is not LocalExpression assignedLocal ||
                    firstUse.Left is not LocalExpression usedLocal ||
                    assignedLocal.Slot != usedLocal.Slot)
                {
                    continue;
                }

                // C 为真后立即执行的下一项就是对 v 的读取，因此替换为赋值表达式
                // 精确保留 `C && (v = value) ...` 的短路顺序。
                firstUse.Left = assignment;
                assignment.Parent = firstUse;
                block.Statements.RemoveAt(index);
                index--;
            }
        }

        private static bool TryGetGuardedLocalAssignment(
            IAstNode node, out IfStatement guard, out BinaryExpression assignment)
        {
            guard = node as IfStatement;
            assignment = null;
            if (guard == null || guard.Else != null ||
                guard.Then is not BlockStatement { Statements.Count: 1 } body ||
                !TryGetAssignment(body.Statements[0], out assignment) ||
                assignment.Left is not LocalExpression)
            {
                guard = null;
                assignment = null;
                return false;
            }

            return true;
        }

        private static bool TryGetAssignment(
            IAstNode node, out BinaryExpression assignment)
        {
            var expression = node switch
            {
                Expression direct => direct,
                ExpressionStatement statement => statement.Expression,
                _ => null
            };
            assignment = expression as BinaryExpression;
            return assignment?.Op == BinaryOp.Assign;
        }

        private static void CollectConjunctionTerms(
            Expression expression, ICollection<Expression> terms)
        {
            if (expression is BinaryExpression { Op: BinaryOp.LogicAnd } conjunction)
            {
                CollectConjunctionTerms(conjunction.Left, terms);
                CollectConjunctionTerms(conjunction.Right, terms);
                return;
            }

            terms.Add(expression);
        }
    }
}
