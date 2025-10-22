using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Visitors
{
    internal abstract class BaseVisitor : IVisitor
    {
        public void Visit(IAstNode node)
        {
            if (node == null)
            {
                return;
            }
            switch (node)
            {
                //Expression
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
                case DeleteExpression deleteExpression:
                    VisitDeleteExpr(deleteExpression);
                    break;
                case UnaryExpression unaryExpression:
                    VisitUnaryExpr(unaryExpression);
                    break;
                case ConditionExpression conditionExpression:
                    VisitConditionExpr(conditionExpression);
                    break;
                case ExpressionStatement expressionStatement:
                    VisitExpressionStmt(expressionStatement);
                    break;
                case BlockStatement blockStatement:
                    VisitBlockStmt(blockStatement);
                    break;
                case GotoExpression gotoExpression:
                    VisitGotoExpr(gotoExpression);
                    break;
                case ReturnExpression returnExpression:
                    VisitReturnExpr(returnExpression);
                    break;
                case ThrowExpression throwExpression:
                    VisitThrowExpr(throwExpression);
                    break;
                case PropertyAccessExpression propertyAccessExpression:
                    VisitPropertyAccessExpr(propertyAccessExpression);
                    break;
                case PhiExpression phiExpression:
                    VisitPhiExpr(phiExpression);
                    break;
                case Expression expression:
                    VisitExpr(expression);
                    break;

                //Statement
                case ForStatement forStatement:
                    VisitForStmt(forStatement);
                    break;
                case IfStatement ifStatement:
                    VisitIfStmt(ifStatement);
                    break;
                case DoWhileStatement doWhileStatement:
                    VisitDoWhileStmt(doWhileStatement);
                    break;
                case WhileStatement whileStatement:
                    VisitWhileStmt(whileStatement);
                    break;
                case BreakStatement breakStatement:
                    VisitBreakStmt(breakStatement);
                    break;
                case ContinueStatement continueStatement:
                    VisitContinueStmt(continueStatement);
                    break;
                case TryStatement tryStatement:
                    VisitTryStmt(tryStatement);
                    break;
                case Statement statement:
                    VisitStmt(statement);
                    break;
                default:
                    break;
            }
        }

        internal virtual void VisitStmt(Statement statement)
        {
        }


        internal virtual void VisitGotoExpr(GotoExpression gotoExpression)
        {
        }

        internal virtual void VisitExpr(Expression expression)
        {
        }

        internal virtual void VisitPhiExpr(PhiExpression phi)
        {
        }

        internal virtual void VisitWhileStmt(WhileStatement whileStmt)
        {
        }

        internal virtual void VisitDoWhileStmt(DoWhileStatement doWhile)
        {
        }

        internal virtual void VisitPropertyAccessExpr(PropertyAccessExpression prop)
        {
        }

        internal virtual void VisitThrowExpr(ThrowExpression throwExpr)
        {
        }

        internal virtual void VisitBreakStmt(BreakStatement breakStmt)
        {
        }

        internal virtual void VisitContinueStmt(ContinueStatement continueStmt)
        {
        }

        internal virtual void VisitTryStmt(TryStatement tryStmt)
        {
        }

        internal virtual void VisitConditionExpr(ConditionExpression condition)
        {
        }

        internal virtual void VisitIfStmt(IfStatement ifStmt)
        {
        }

        internal virtual void VisitReturnExpr(ReturnExpression ret)
        {
        }

        internal virtual void VisitUnaryExpr(UnaryExpression unary)
        {
        }

        internal virtual void VisitForStmt(ForStatement forStmt)
        {
        }

        internal virtual void VisitDeleteExpr(DeleteExpression delete)
        {
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

        internal virtual void VisitIdentifierExpr(IdentifierExpression id)
        {
        }

        internal virtual void VisitBinaryExpr(BinaryExpression bin)
        {
            Visit(bin.Left);
            Visit(bin.Right);
        }

        internal virtual void VisitBlockStmt(BlockStatement block)
        {
            foreach (var st in block.Statements)
            {
                Visit(st);
            }
        }
    }
}