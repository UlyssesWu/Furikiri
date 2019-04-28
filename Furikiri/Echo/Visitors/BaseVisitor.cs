using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Visitors
{
    internal class BaseVisitor : IVisitor
    {
        public void Visit(IAstNode node)
        {
            switch (node)
            {
                case BinaryExpression binaryExpression:
                    VisitBinaryExpr(binaryExpression);
                    break;
                case IdentifierExpression identifierExpression:
                    VisitIdentifierExpr(identifierExpression);
                    break;
                case InvokeExpression invokeExpression:
                    VisitInvokeExpr(invokeExpression);
                    break;
                case ConstantExpression constantExpression:
                    VisitConstantExpr(constantExpression);
                    break;
                case LocalExpression localExpression:
                    VisitLocalExpr(localExpression);
                    break;
                case ExpressionStatement expressionStatement:
                    VisitExpressionStmt(expressionStatement);
                    break;
                case BlockStatement blockStatement:
                    VisitBlockStmt(blockStatement);
                    break;
                case null:
                    break;
                case ConditionExpression conditionExpression:
                    break;


                case GotoExpression gotoExpression:
                    break;



                case ReturnExpression returnExpression:
                    break;
                case UnaryExpression unaryExpression:
                    break;
                case Expression expression:
                    break;

                case DoWhileStatement doWhileStatement:
                    break;
                case ForStatement forStatement:
                    break;
                case IfStatement ifStatement:
                    break;
                case Statement statement:
                    break;
            }
        }

        internal virtual void VisitLocalExpr(LocalExpression local)
        {
        }

        internal virtual void VisitExpressionStmt(ExpressionStatement expression)
        {
        }

        internal virtual void VisitConstantExpr(ConstantExpression constant)
        {
        }

        internal virtual void VisitInvokeExpr(InvokeExpression invoke)
        {
        }

        internal virtual void VisitBlockStmt(BlockStatement block)
        {
            foreach (var st in block.Statements)
            {
                Visit(st);
            }
        }

        internal virtual void VisitIdentifierExpr(IdentifierExpression id)
        {
        }

        internal virtual void VisitBinaryExpr(BinaryExpression bin)
        {
            Visit(bin.Left);
            Visit(bin.Right);
        }
    }
}