using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Visitors
{
    class BaseVisitor : IVisitor
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

        public virtual void VisitIdentifierExpr(IdentifierExpression id)
        {
        }

        public virtual void VisitBinaryExpr(BinaryExpression bin)
        {
        }
    }
}