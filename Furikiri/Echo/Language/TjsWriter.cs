using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Visitors;
using Furikiri.Emit;

namespace Furikiri.Echo.Language
{
    /// <summary>
    /// Output TJS2 Code from AST in plain text without rendering
    /// </summary>
    internal class TjsWriter : BaseVisitor
    {
        private IFormatter _formatter;
        private IndentedTextWriter _writer;
        public Dictionary<Method, BlockStatement> MethodRefs = new Dictionary<Method, BlockStatement>();

        /// <summary>
        /// Do not write return if there is nothing to return
        /// </summary>
        public bool HideVoidReturn { get; set; } = true;

        /// <summary>
        /// Add new line after if/for/while etc.
        /// </summary>
        public bool NewLinesAfterStructureControlStatements { get; set; } = true;

        public TjsWriter(StringWriter writer)
        {
            _writer = new IndentedTextWriter(writer);
            _formatter = new TjsTextFormatter(_writer);
        }

        private void AddNewLineAfterStructCtrlStmt()
        {
            if (NewLinesAfterStructureControlStatements)
            {
                _formatter.WriteLine();
            }
        }

        private void WriteSignature(Method method)
        {
            if (method.Object.ContextType == TjsContextType.TopLevel)
            {
                return;
            }

            //_formatter.WriteKeyword(method.Object.ContextType.ContextTypeName());
            _formatter.WriteKeyword("function");
            _formatter.WriteSpace();
            if (method.Object.ContextType == TjsContextType.Function)
            {
                _formatter.WriteIdentifier(method.Name);
            }

            _formatter.WriteToken("(");
            //parameters
            var paramList = method.Vars.Where(kv => kv.Value.IsParameter).OrderByDescending(kv => kv.Key)
                .Select(kv => kv.Value).ToList();
            if (paramList.Count > 0)
            {
                for (int i = 0; i < paramList.Count - 1; i++)
                {
                    _formatter.WriteIdentifier(paramList[i].ToString());
                    _formatter.WriteToken(",");
                    _formatter.WriteSpace();
                }

                _formatter.WriteIdentifier(paramList[paramList.Count - 1].ToString());
            }

            _formatter.WriteToken(")");
            _formatter.WriteLine();
        }

        /// <summary>
        /// Write Function
        /// </summary>
        /// <param name="method"></param>
        /// <param name="block"></param>
        public void WriteFunction(Method method, BlockStatement block)
        {
            WriteSignature(method);
            WriteMethodBody(method, block, method.Object.ContextType != TjsContextType.TopLevel);
        }

        /// <summary>
        /// Write Method Body
        /// <para>Method can be function, property getter/setter etc.</para>
        /// </summary>
        /// <param name="method"></param>
        /// <param name="block"></param>
        private void WriteMethodBody(Method method, BlockStatement block, bool braces = true)
        {
            if (braces)
            {
                _formatter.WriteToken("{");
                _formatter.WriteLine();
                _formatter.Indent();
            }


            WriteBlock(block);

            if (braces)
            {
                _formatter.Outdent();
                //_formatter.WriteLine();
                _formatter.WriteToken("}");
            }
        }

        public void WriteLine(string line = null)
        {
            if (string.IsNullOrEmpty(line))
            {
                _writer.WriteLine();
            }
            else
            {
                _writer.WriteLine(line);
            }
        }

        public void WriteLicense()
        {
            _formatter.WriteComment(Const.LicenseInfo);
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
                if (bin.Left is IdentifierExpression id && id.Instance is IdentifierExpression instance &&
                    !instance.HideInstance)
                {
                    //this is to prevent adding `var` before `System.var = a;`
                    //do nothing
                }
                else
                {
                    _formatter.WriteIdentifier("var");
                    _formatter.WriteSpace();
                }
            }

            bool needBrackets = bin.NeedBrackets();
            if (bin.IsSelfAssignment)
            {
                needBrackets = false;
                if (bin.Op.CanSelfAssign())
                {
                    Visit(bin.Left);
                    _formatter.WriteSpace();
                    _formatter.WriteToken(bin.Op.ToSelfAssignSymbol());
                    _formatter.WriteSpace();
                    Visit(bin.Right);
                    return;
                }

                if (bin.Op != BinaryOp.Assign) //do not make var a = a = b;
                {
                    Visit(bin.Left);
                    _formatter.WriteSpace();
                    _formatter.WriteToken("=");
                    _formatter.WriteSpace();
                }
            }

            if (needBrackets)
            {
                _formatter.WriteToken("(");
            }

            Visit(bin.Left);
            _formatter.WriteSpace();
            _formatter.WriteToken(bin.Op.ToSymbol());
            _formatter.WriteSpace();
            Visit(bin.Right);
            if (needBrackets)
            {
                _formatter.WriteToken(")");
            }
        }

        internal override void VisitInvokeExpr(InvokeExpression invoke)
        {
            if (invoke.InvokeType == InvokeType.RegExpCompile)
            {
                _formatter.WriteIdentifier(invoke.ToRegExp());
                return;
            }

            if (invoke.InvokeType == InvokeType.Ctor)
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

            if (invoke.MethodExpression != null)
            {
                Visit(invoke.MethodExpression);
            }
            else
            {
                _formatter.WriteIdentifier(invoke.Method);
            }
            _formatter.WriteToken("(");
            for (var i = 0; i < invoke.Parameters.Count; i++)
            {
                var para = invoke.Parameters[i];
                Visit(para);
                if (i < invoke.Parameters.Count - 1)
                {
                    _formatter.Write(", ");
                }
            }

            _formatter.WriteToken(")");
        }

        internal override void VisitPropertyAccessExpr(PropertyAccessExpression prop)
        {
            if (!prop.HideInstance)
            {
                Visit(prop.Instance);
                //_formatter.WriteToken(".");
                _formatter.WriteToken("[");
                Visit(prop.Property);
                _formatter.WriteToken("]");
            }
            else
            {
                _formatter.WriteToken("[");
                Visit(prop.Property);
                _formatter.WriteToken("]");
            }
        }

        internal override void VisitThrowExpr(ThrowExpression throwExpr)
        {
            _formatter.WriteKeyword("throw");
            _formatter.WriteSpace();
            Visit(throwExpr.Target);
        }

        internal override void VisitConstantExpr(ConstantExpression constant)
        {
            if (constant.DataType == TjsVarType.Object && MethodRefs != null && constant.Variant is TjsCodeObject obj)
            {
                var method = MethodRefs.FirstOrDefault(m => m.Key.Object == obj.Object);
                if (method.Key != null && method.Key.IsLambda)
                {
                    WriteFunction(method.Key, method.Value);
                    return;
                }
            }

            _formatter.WriteLiteral(constant.ToString());
        }

        internal override void VisitExpressionStmt(ExpressionStatement expression)
        {
            int pos = _formatter.CurrentPosition;
            if (expression.Expression is IOperation bin)
            {
                bin.IsSelfAssignment = true;
            }

            Visit(expression.Expression);
            if (_formatter.CurrentPosition == pos)
            {
                //wrote nothing, no new line
                return;
            }

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
            if (unary.IsSelfAssignment && !unary.Op.CanSelfAssign())
            {
                Visit(unary.Target);
                _formatter.WriteSpace();
                _formatter.WriteIdentifier("=");
                _formatter.WriteSpace();
            }

            switch (unary.Op)
            {
                case UnaryOp.Inc:
                case UnaryOp.Dec:
                    //if (unary.Instance != null && !unary.HideInstance)
                    //{
                    //    Visit(unary.Instance);
                    //    _formatter.WriteToken(".");
                    //}
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
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    Visit(unary.Target);
                    break;
                case UnaryOp.TypeOf:
                case UnaryOp.Invalidate:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    _formatter.WriteSpace();
                    Visit(unary.Target);
                    break;
                default:
                    Visit(unary.Target);
                    break;
            }
        }

        internal override void VisitConditionExpr(ConditionExpression condition)
        {
            Visit(condition.Condition);
        }

        internal override void VisitBreakStmt(BreakStatement breakStmt)
        {
            _formatter.WriteKeyword("break");
            _formatter.WriteToken(";");
            _formatter.WriteLine();
        }

        internal override void VisitContinueStmt(ContinueStatement continueStmt)
        {
            _formatter.WriteKeyword("continue");
            _formatter.WriteToken(";");
            _formatter.WriteLine();
        }

        internal override void VisitReturnExpr(ReturnExpression ret)
        {
            if (HideVoidReturn && ret.Return == null)
            {
                return;
            }

            _formatter.WriteKeyword("return");
            if (ret.Return != null)
            {
                _formatter.WriteSpace();
                Visit(ret.Return);
            }
        }

        internal override void VisitIfStmt(IfStatement ifStmt)
        {
            _formatter.WriteKeyword("if");
            _formatter.WriteSpace();
            _formatter.WriteToken("(");
            Visit(ifStmt.Condition);
            _formatter.WriteToken(")");
            _formatter.WriteLine();

            _formatter.WriteStartBlock();
            Visit(ifStmt.Then);
            _formatter.WriteEndBlock();
            if (ifStmt.Else != null)
            {
                _formatter.WriteKeyword("else");
                _formatter.WriteSpace();
                if (ifStmt.Else is IfStatement elseIf && elseIf.IsElseIf)
                {
                    VisitIfStmt(elseIf);
                }
                else
                {
                    _formatter.WriteLine();
                    _formatter.WriteStartBlock();
                    Visit(ifStmt.Else);
                    _formatter.WriteEndBlock();
                }
            }

            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitForStmt(ForStatement forStmt)
        {
            _formatter.WriteKeyword("for");
            _formatter.WriteSpace();
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
            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitDoWhileStmt(DoWhileStatement doWhile)
        {
            _formatter.WriteKeyword("do");
            _formatter.WriteLine();
            _formatter.WriteStartBlock();
            Visit(doWhile.Body);
            _formatter.WriteEndBlock();
            _formatter.WriteKeyword("while");
            _formatter.WriteSpace();
            _formatter.WriteToken("(");
            if (doWhile.Condition != null)
            {
                Visit(doWhile.Condition);
            }
            else
            {
                _formatter.WriteKeyword("true");
            }

            _formatter.WriteToken(")");
            _formatter.WriteToken(";");
            _formatter.WriteLine();

            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitWhileStmt(WhileStatement whileStmt)
        {
            _formatter.WriteKeyword("while");
            _formatter.WriteSpace();
            _formatter.WriteToken("(");
            if (whileStmt.Condition != null)
            {
                Visit(whileStmt.Condition);
            }
            else
            {
                _formatter.WriteKeyword("true");
            }

            _formatter.WriteToken(")");
            _formatter.WriteLine();

            _formatter.WriteStartBlock();
            Visit(whileStmt.Body);
            _formatter.WriteEndBlock();
            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitPhiExpr(PhiExpression phi)
        {
            //FIXME: This is a fake implementation
            _formatter.Write(phi.ToString());
        }
    }
}