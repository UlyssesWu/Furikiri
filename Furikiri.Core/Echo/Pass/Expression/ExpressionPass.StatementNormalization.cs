using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        /// <summary>
        /// VM 会把 `outer = inner = call()` 编译为一次 call 后连续两条属性/局部写入。
        /// 表达式传播让两条赋值共享同一个右值 AST；若直接逐条输出，调用或构造器会
        /// 被执行两次。按连续共享引用分组，从最先执行的内层赋值向外重新嵌套。
        /// </summary>
        private static void CollapseChainedAssignments(List<IAstNode> statements)
        {
            if (statements == null || statements.Count < 2)
            {
                return;
            }

            for (var index = 0; index < statements.Count - 1; index++)
            {
                if (statements[index] is not BinaryExpression first ||
                    first.Op != BinaryOp.Assign)
                {
                    continue;
                }

                var sharedRight = first.Right;
                var end = index + 1;
                while (end < statements.Count &&
                       statements[end] is BinaryExpression next &&
                       next.Op == BinaryOp.Assign &&
                       ReferenceEquals(next.Right, sharedRight))
                {
                    end++;
                }

                if (end == index + 1)
                {
                    continue;
                }

                // `var` 只能位于最外层赋值。编译器生成的链式声明满足此约束；
                // 若遇到不合法的中间声明则保守保留原语句，避免生成嵌套 var。
                if (statements.Skip(index).Take(end - index - 1)
                    .OfType<BinaryExpression>().Any(assignment => assignment.IsDeclaration))
                {
                    continue;
                }

                first.Right.Parent = first;
                Expression chain = first;
                for (var current = index + 1; current < end; current++)
                {
                    var assignment = (BinaryExpression)statements[current];
                    chain = new BinaryExpression(
                        assignment.Left, chain, BinaryOp.Assign)
                    {
                        IsDeclaration = assignment.IsDeclaration
                    };
                }

                statements[index] = chain;
                statements.RemoveRange(index + 1, end - index - 1);
            }
        }

        /// <summary>
        /// 恢复 `with (call())` 一类控制对象的单次求值。一个调用结果寄存器可能被
        /// 多条成员读取指令共享；表达式状态中的这些读取会指向同一个调用节点。
        /// 只按节点身份合并，两个文本相同但分别执行的调用不会被误认为同一次求值。
        /// </summary>
        private static void HoistRepeatedMemberReceiverInvocations(
            DecompileContext context, List<IAstNode> statements)
        {
            if (context == null || statements == null || statements.Count == 0)
            {
                return;
            }

            var occurrences = new List<(
                InvokeExpression Invocation,
                Action<Expression> Replace,
                string Member,
                int StatementIndex)>();

            void Walk(Expression expression, int statementIndex)
            {
                if (expression == null)
                {
                    return;
                }

                void RecordReceiver(
                    InvokeExpression receiver, Action<Expression> replace, string member)
                {
                    occurrences.Add((receiver, replace, member ?? string.Empty, statementIndex));
                    Walk(receiver.Instance, statementIndex);
                    Walk(receiver.MethodExpression, statementIndex);
                    foreach (var parameter in receiver.Parameters)
                    {
                        Walk(parameter, statementIndex);
                    }
                }

                switch (expression)
                {
                    case IdentifierExpression identifier:
                        if (identifier.Instance is InvokeExpression identifierReceiver)
                        {
                            RecordReceiver(identifierReceiver,
                                replacement => identifier.Instance = replacement,
                                identifier.Name);
                        }
                        else
                        {
                            Walk(identifier.Instance, statementIndex);
                        }
                        break;
                    case PropertyAccessExpression property:
                        if (property.Instance is InvokeExpression propertyReceiver)
                        {
                            RecordReceiver(propertyReceiver,
                                replacement => property.Instance = replacement,
                                property.Property?.ToString());
                        }
                        else
                        {
                            Walk(property.Instance, statementIndex);
                        }
                        Walk(property.Property, statementIndex);
                        break;
                    case InvokeExpression invocation:
                        if (invocation.InvokeType == InvokeType.RegExpCompile)
                        {
                            // RegExp 构造器与紧随其后的 _compile 是一个正则字面量
                            // 的 VM 展开形式，写出时会合并为 `/.../flags`。构造器实例
                            // 不是第二次可见消费，不能因 AST 内部引用而额外缓存并输出。
                            Walk(invocation.MethodExpression, statementIndex);
                            foreach (var parameter in invocation.Parameters)
                            {
                                Walk(parameter, statementIndex);
                            }
                            break;
                        }

                        if (invocation.Instance is InvokeExpression invocationReceiver)
                        {
                            RecordReceiver(invocationReceiver,
                                replacement => invocation.Instance = replacement,
                                invocation.MethodName ?? invocation.MethodExpression?.ToString());
                        }
                        else
                        {
                            Walk(invocation.Instance, statementIndex);
                        }
                        Walk(invocation.MethodExpression, statementIndex);
                        foreach (var parameter in invocation.Parameters)
                        {
                            Walk(parameter, statementIndex);
                        }
                        break;
                    case BinaryExpression binary:
                        Walk(binary.Left, statementIndex);
                        Walk(binary.Right, statementIndex);
                        break;
                    case UnaryExpression unary:
                        Walk(unary.Target, statementIndex);
                        break;
                    case ConditionExpression condition:
                        Walk(condition.Condition, statementIndex);
                        break;
                    case ReturnExpression ret:
                        Walk(ret.Return, statementIndex);
                        break;
                    case ThrowExpression thrown:
                        Walk(thrown.Target, statementIndex);
                        break;
                    case DeleteExpression delete:
                        Walk(delete.Instance, statementIndex);
                        Walk(delete.IdentifierExpression, statementIndex);
                        break;
                    case PhiExpression phi:
                        foreach (var possible in phi.PossibleExpressions)
                        {
                            Walk(possible, statementIndex);
                        }
                        break;
                }
            }

            for (var index = 0; index < statements.Count; index++)
            {
                if (statements[index] is Expression expression)
                {
                    Walk(expression, index);
                }
            }

            var groups = new List<List<(
                InvokeExpression Invocation,
                Action<Expression> Replace,
                string Member,
                int StatementIndex)>>();
            foreach (var occurrence in occurrences)
            {
                var group = groups.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate[0].Invocation, occurrence.Invocation));
                if (group == null)
                {
                    group = new List<(
                        InvokeExpression Invocation,
                        Action<Expression> Replace,
                        string Member,
                        int StatementIndex)>();
                    groups.Add(group);
                }
                group.Add(occurrence);
            }

            var insertionOffset = 0;
            foreach (var group in groups
                         .Where(group => group.Count >= 2)
                         .OrderBy(group => group.Min(item => item.StatementIndex)))
            {
                var variable = ExpressionValueIdentity.CreateSyntheticVariable(context);
                var invocation = group[0].Invocation;
                foreach (var occurrence in group)
                {
                    occurrence.Replace(new LocalExpression(variable));
                }

                var declaration = new BinaryExpression(
                    new LocalExpression(variable), invocation, BinaryOp.Assign)
                {
                    IsDeclaration = true
                };
                statements.Insert(
                    group.Min(item => item.StatementIndex) + insertionOffset,
                    declaration);
                insertionOffset++;
            }
        }

        private static Expression BuildSwapOperand(
            DecompileContext context, IReadOnlyDictionary<int, Expression> expressions,
            SwapOperandKind kind, short registerSlot, short objectSlot,
            short memberSlot, IReadOnlyList<string> memberPath)
        {
            if (kind == SwapOperandKind.Register)
            {
                return GetLocalExpression(context, registerSlot);
            }

            var instance = expressions[objectSlot];
            if (kind == SwapOperandKind.IndirectProperty)
            {
                if (memberPath != null)
                {
                    foreach (var memberName in memberPath)
                    {
                        instance = new IdentifierExpression(memberName)
                        {
                            Instance = instance
                        };
                    }
                }
                return new PropertyAccessExpression(
                    expressions[memberSlot], instance);
            }

            Expression result = instance;
            foreach (var memberName in memberPath)
            {
                result = new IdentifierExpression(memberName)
                {
                    Instance = result
                };
            }
            return result;
        }
    }
}
