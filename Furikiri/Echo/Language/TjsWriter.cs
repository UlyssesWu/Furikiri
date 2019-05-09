using System.CodeDom.Compiler;
using System.IO;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Visitors;

namespace Furikiri.Echo.Language
{
    /// <summary>
    /// Output TJS2 Code from AST in plain text without rendering
    /// </summary>
    internal class TjsWriter : BaseVisitor
    {
        private IFormatter _formatter;
        private IndentedTextWriter _writer;

        public TjsWriter(StringWriter writer)
        {
            _writer = new IndentedTextWriter(writer);
            _formatter = new TjsTextFormatter(_writer);
        }

        public void WriteBlock(BlockStatement st)
        {
            Visit(st);
        }

        internal override void VisitIdentifierExpr(IdentifierExpression id)
        {
            _formatter.WriteIdentifier(id.FullName);
        }

        internal override void VisitBinaryExpr(BinaryExpression bin)
        {
            if (bin.IsDeclaration)
            {
                _formatter.WriteIdentifier("var");
                _formatter.WriteSpace();
            }

            Visit(bin.Left);
            _formatter.WriteSpace();
            _formatter.WriteToken(bin.Op.ToSymbol());
            _formatter.WriteSpace();
            Visit(bin.Right);
        }

        internal override void VisitInvokeExpr(InvokeExpression invoke)
        {
            if (invoke.IsCtor)
            {
                //TODO: quick ctor syntax for Dictionary, Array
                _formatter.WriteKeyword("new");
                _formatter.WriteSpace();
            }

            if (invoke.Instance != null && !invoke.HideInstance)
            {
                Visit(invoke.Instance);
                _formatter.WriteToken(".");
            }

            _formatter.WriteIdentifier(invoke.Method);
            _formatter.WriteToken("(");
            for (var i = 0; i < invoke.Parameters.Count; i++)
            {
                var para = invoke.Parameters[i];
                Visit(para);
                if (i < invoke.Parameters.Count - 1)
                {
                    _formatter.Write(",");
                }
            }

            _formatter.WriteToken(")");
        }

        internal override void VisitConstantExpr(ConstantExpression constant)
        {
            _formatter.WriteLiteral(constant.ToString());
        }

        internal override void VisitExpressionStmt(ExpressionStatement expression)
        {
            Visit(expression.Expression);
            _formatter.WriteToken(";");
            _formatter.WriteLine();
        }

        internal override void VisitLocalExpr(LocalExpression local)
        {
            _formatter.WriteIdentifier(local.ToString());
        }

        internal override void VisitDeleteExpr(DeleteExpression delete)
        {
            _formatter.WriteKeyword("delete");
            _formatter.WriteSpace();
            if (delete.Instance != null && !delete.HideInstance)
            {
                Visit(delete.Instance);
                _formatter.WriteToken(".");
            }

            _formatter.WriteIdentifier(delete.Identifier);
        }

        internal override void VisitUnaryExpr(UnaryExpression unary)
        {
            switch (unary.Op)
            {
                case UnaryOp.Inc:
                case UnaryOp.Dec:
                    Visit(unary.Target);
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    break;
                case UnaryOp.InvertSign:
                case UnaryOp.Not:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    Visit(unary.Target);
                    break;
                case UnaryOp.ToInt:
                case UnaryOp.ToReal:
                case UnaryOp.ToString:
                case UnaryOp.ToNumber:
                case UnaryOp.ToByteArray:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    Visit(unary.Target);
                    break;
                case UnaryOp.IsTrue:
                    Visit(unary.Target);
                    break;
                case UnaryOp.IsFalse:
                    _formatter.WriteToken("!");
                    Visit(unary.Target);
                    break;
                default:
                    Visit(unary.Target);
                    break;
            }
        }

        internal override void VisitIfStmt(IfStatement ifStmt)
        {
            _formatter.WriteIdentifier("if");
            _formatter.WriteToken("(");
            Visit(ifStmt.Condition);
            _formatter.WriteToken(")");
            _formatter.WriteLine();

            _formatter.WriteStartBlock();
            Visit(ifStmt.Then);
            _formatter.WriteEndBlock();
            if (ifStmt.Else != null && ifStmt.Else.Statements.Count > 0)
            {
                _formatter.WriteIdentifier("else");
                _formatter.WriteStartBlock();
                Visit(ifStmt.Else);
                _formatter.WriteEndBlock();
            }
        }

        internal override void VisitForStmt(ForStatement forStmt)
        {
            _formatter.WriteIdentifier("for");
            _formatter.WriteToken("(");

            Visit(forStmt.Initializer);
            _formatter.WriteToken(";");
            _formatter.WriteSpace();

            Visit(forStmt.Condition);
            _formatter.WriteToken(";");
            _formatter.WriteSpace();

            Visit(forStmt.Increment);
            //_formatter.WriteSpace();

            _formatter.WriteToken(")");
            _formatter.WriteLine();
            _formatter.WriteStartBlock();
            Visit(forStmt.Body);
            _formatter.WriteEndBlock();
        }
    }
}