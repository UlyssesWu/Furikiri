using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    class StatementCollectPass : IPass
    {
        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            Dictionary<Block, List<IAstNode>> blockStmts = new Dictionary<Block, List<IAstNode>>();
            foreach (var block in context.Blocks)
            {
                var newStmts = new List<IAstNode>();
                var loop = context.LoopSet.FirstOrDefault(l => l.Header == block);
                if (loop != null)
                {
                    // 循环头若已被外层 if/try 隐藏，循环语句已经嵌入该结构，
                    // 不能在顶层再次输出。旧的强制取消 Hidden 会造成循环重复或提升。
                    if (loop.Parent == null && !block.Hidden)
                    {
                        newStmts.Add(loop.MaterializedStatement ?? loop.LoopLogic.ToStatement());
                    }
                }
                else
                {
                    foreach (var node in block.Statements)
                    {
                        switch (node)
                        {
                            case GotoExpression _:
                            case ConditionExpression _:
                                break;
                            case Expression expr:
                                newStmts.Add(new ExpressionStatement(expr));
                                break;
                            case Statement stmt:
                                newStmts.Add(stmt);
                                break;
                        }
                    }
                }

                blockStmts[block] = newStmts;
            }

            foreach (var block in context.Blocks)
            {
                if (block.Hidden)
                {
                    continue;
                }

                statement.Statements.AddRange(blockStmts[block]);
            }

            NormalizeSharedCaseBranches(statement);

            return statement;
        }

        /// <summary>
        /// 修复多个相等比较共用同一源码分支时留下的结构化外壳。
        /// 仅处理同一比较目标上的 ==/!= case，避免把普通空分支随意改写。
        /// </summary>
        private static void NormalizeSharedCaseBranches(BlockStatement block)
        {
            if (block == null)
            {
                return;
            }

            for (var index = 0; index < block.Statements.Count; index++)
            {
                NormalizeNode(block.Statements[index]);

                if (index + 1 < block.Statements.Count &&
                    TryWrapDefaultAssignment(
                        block.Statements[index], block.Statements[index + 1], out var guardedAssignment))
                {
                    block.Statements[index] = guardedAssignment;
                    block.Statements.RemoveAt(index + 1);
                    NormalizeIfStatement(guardedAssignment);
                    continue;
                }

                if (index + 1 < block.Statements.Count &&
                    block.Statements[index] is ExpressionStatement expressionStatement &&
                    block.Statements[index + 1] is IfStatement followingIf)
                {
                    var first = UnwrapCondition(expressionStatement.Expression);
                    var second = UnwrapCondition(followingIf.Condition);
                    if (IsEqualComparison(first) && IsEqualComparison(second) &&
                        HasSameComparisonTarget(first, second))
                    {
                        // 裸比较在 TJS2 中没有副作用；它紧邻同目标 if 时是共享
                        // case 的首项被脱离控制流，合回 OR 后再删除裸表达式。
                        followingIf.Condition = first.Or(second);
                        block.Statements.RemoveAt(index);
                        index--;
                    }
                }
            }
        }

        private static bool TryWrapDefaultAssignment(
            IAstNode conditionNode, IAstNode assignmentNode, out IfStatement guarded)
        {
            guarded = null;
            if (conditionNode is not ExpressionStatement { Expression: ConditionExpression wrapper } ||
                assignmentNode is not ExpressionStatement
                {
                    Expression: BinaryExpression { Op: BinaryOp.Assign } assignment
                })
            {
                return false;
            }

            var condition = UnwrapCondition(wrapper);
            if (condition is not BinaryExpression comparison ||
                comparison.Op is not (BinaryOp.Equal or BinaryOp.Congruent) ||
                comparison.Right?.ToString() != "void" ||
                comparison.Left?.ToString() != assignment.Left?.ToString())
            {
                return false;
            }

            // `member === void; member = default` 是默认值 if 的条件壳被脱离
            // 分支后的形态。赋值目标必须与比较左值完全相同，避免把普通相邻
            // 比较和无关赋值错误绑定。
            var body = new BlockStatement();
            body.Statements.Add(assignmentNode);
            guarded = new IfStatement(condition, body, null);
            return true;
        }

        private static void NormalizeNode(IAstNode node)
        {
            if (node is Statement statement)
            {
                NormalizeStatement(statement);
            }
        }

        private static void NormalizeStatement(Statement statement)
        {
            switch (statement)
            {
                case BlockStatement block:
                    NormalizeSharedCaseBranches(block);
                    break;
                case IfStatement ifStatement:
                    NormalizeIfStatement(ifStatement);
                    break;
                case ForStatement forStatement:
                    NormalizeSharedCaseBranches(forStatement.Body);
                    break;
                case WhileStatement whileStatement:
                    NormalizeSharedCaseBranches(whileStatement.Body);
                    break;
                case DoWhileStatement doWhileStatement:
                    NormalizeSharedCaseBranches(doWhileStatement.Body);
                    break;
                case TryStatement tryStatement:
                    NormalizeSharedCaseBranches(tryStatement.Try);
                    NormalizeSharedCaseBranches(tryStatement.Finally);
                    break;
            }
        }

        private static void NormalizeIfStatement(IfStatement ifStatement)
        {
            var nested = GetSingleIf(ifStatement.Then);
            var outerCondition = UnwrapCondition(ifStatement.Condition);
            var nestedCondition = UnwrapCondition(nested?.Condition);
            if (nested != null && IsEmpty(nested.Then) &&
                ifStatement.Else != null && nested.Else != null &&
                IsNotEqualComparison(outerCondition) &&
                IsEqualComparison(nestedCondition) &&
                HasSameComparisonTarget(outerCondition, nestedCondition))
            {
                // if (x != A) { if (x == B) {} else E } else Bdy
                // 是 (x == A || x == B) 共用 Bdy 被拆坏后的形态。
                ifStatement.Condition = outerCondition.Invert().Or(nestedCondition);
                ifStatement.Then = ifStatement.Else;
                ifStatement.Else = nested.Else;
            }

            NormalizeStatement(ifStatement.Then);
            NormalizeStatement(ifStatement.Else);

            if (IsEmpty(ifStatement.Then) && !IsEmpty(ifStatement.Else))
            {
                // if (cond) {} else body 与 if (!cond) body 完全等价；统一后既避免
                // 空分支，也能让后续共享 case 归一化看到真实分支体。
                ifStatement.Condition = UnwrapCondition(ifStatement.Condition).Invert();
                ifStatement.Then = ifStatement.Else;
                ifStatement.Else = null;
            }
            else if (IsEmpty(ifStatement.Else))
            {
                ifStatement.Else = null;
            }
        }

        private static IfStatement GetSingleIf(Statement statement)
        {
            if (statement is IfStatement direct)
            {
                return direct;
            }

            return statement is BlockStatement { Statements.Count: 1 } block &&
                   block.Statements[0] is IfStatement nested
                ? nested
                : null;
        }

        private static bool IsEmpty(Statement statement)
        {
            return statement == null ||
                   statement is BlockStatement block && block.Statements.Count == 0;
        }

        private static Expression UnwrapCondition(Expression expression)
        {
            while (expression is ConditionExpression condition)
            {
                expression = condition.Condition;
            }

            return expression;
        }

        private static bool IsEqualComparison(Expression expression)
        {
            return expression is BinaryExpression binary &&
                   binary.Op is BinaryOp.Equal or BinaryOp.Congruent;
        }

        private static bool IsNotEqualComparison(Expression expression)
        {
            return expression is BinaryExpression binary &&
                   binary.Op is BinaryOp.NotEqual or BinaryOp.NotCongruent;
        }

        private static bool HasSameComparisonTarget(Expression first, Expression second)
        {
            return first is BinaryExpression firstBinary &&
                   second is BinaryExpression secondBinary &&
                   firstBinary.Left?.ToString() == secondBinary.Left?.ToString();
        }
    }
}
