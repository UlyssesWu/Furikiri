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
                    break;
                case null:
                    break;
                case ConditionExpression conditionExpression:
                    break;
                case ConstantExpression constantExpression:
                    break;

                case GotoExpression gotoExpression:
                    break;


                case LocalExpression localExpression:
                    break;
                case ReturnExpression returnExpression:
                    break;
                case UnaryExpression unaryExpression:
                    break;
                case Expression expression:
                    break;
                case BlockStatement blockStatement:
                    VisitBlockStmt(blockStatement);
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

        internal virtual void VisitBlockStmt(BlockStatement blockStatement)
        {
            foreach (var st in blockStatement.Statements)
            {
                Visit(st);
            }
        }

        internal virtual void VisitIdentifierExpr(IdentifierExpression id)
        {
        }

        internal virtual void VisitBinaryExpr(BinaryExpression bin)
        {
        }
    }
}