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
            Visit(bin.Left);
            _formatter.WriteSpace();
            _formatter.WriteToken(bin.Op.ToSymbol());
            _formatter.WriteSpace();
            Visit(bin.Right);
        }

        internal override void VisitInvokeExpr(InvokeExpression invoke)
        {
            if (invoke.Caller != null && !invoke.HideCaller)
            {
                Visit(invoke.Caller);
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
    }
}