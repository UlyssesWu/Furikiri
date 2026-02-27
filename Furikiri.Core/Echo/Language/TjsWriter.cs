using System;
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
        private readonly HashSet<string> _declaredLocals = new HashSet<string>();
        private bool _inForInitializer;
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
            var paramList = GetParameterList(method);
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

        private static List<Variable> GetParameterList(Method method)
        {
            return method.Vars.Where(kv => kv.Value.IsParameter)
                .OrderByDescending(kv => kv.Key)
                .Select(kv => kv.Value)
                .ToList();
        }

        /// <summary>
        /// Write Function
        /// </summary>
        /// <param name="method"></param>
        /// <param name="block"></param>
        public void WriteFunction(Method method, BlockStatement block)
        {
            WriteSignature(method);
            WriteMethodBody(block, method.Object.ContextType != TjsContextType.TopLevel);
        }

        public void WriteProperty(Property property, BlockStatement getterBlock, BlockStatement setterBlock)
        {
            _formatter.WriteKeyword("property");
            _formatter.WriteSpace();
            _formatter.WriteIdentifier(property.Name);
            _formatter.WriteLine();
            _formatter.WriteToken("{");
            _formatter.WriteLine();
            _formatter.Indent();

            if (property.Setter != null && setterBlock != null)
            {
                _formatter.WriteKeyword("setter");
                _formatter.WriteToken("(");
                var setterParams = GetParameterList(property.Setter);
                for (var i = 0; i < setterParams.Count; i++)
                {
                    if (i > 0)
                    {
                        _formatter.WriteToken(",");
                        _formatter.WriteSpace();
                    }

                    _formatter.WriteIdentifier(setterParams[i].ToString());
                }

                _formatter.WriteToken(")");
                _formatter.WriteLine();
                WriteMethodBody(setterBlock, true);
                _formatter.WriteLine();
            }

            if (property.Getter != null && getterBlock != null)
            {
                _formatter.WriteKeyword("getter");
                _formatter.WriteToken("()");
                _formatter.WriteLine();
                WriteMethodBody(getterBlock, true);
                _formatter.WriteLine();
            }

            _formatter.Outdent();
            _formatter.WriteToken("}");
            _formatter.WriteLine();
        }

        /// <summary>
        /// Write Method Body
        /// <para>Method can be function, property getter/setter etc.</para>
        /// </summary>
        /// <param name="block"></param>
        /// <param name="braces"></param>
        private void WriteMethodBody(BlockStatement block, bool braces)
        {
            _declaredLocals.Clear();
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

        private void WriteLoopBody(BlockStatement body)
        {
            if (body?.Statements == null || body.Statements.Count == 0)
            {
                return;
            }

            var count = body.Statements.Count;
            if (body.Statements[count - 1] is ContinueStatement)
            {
                count--;
            }

            for (var i = 0; i < count; i++)
            {
                if (body.Statements[i] is ContinueStatement && i < count - 1)
                {
                    // Drop unreachable continue statements that appear before
                    // additional statements in the same loop body sequence.
                    continue;
                }

                Visit(body.Statements[i]);
            }
        }

        internal override void VisitIdentifierExpr(IdentifierExpression id)
        {
            _formatter.WriteIdentifier(id.FullName);
        }

        internal override void VisitBinaryExpr(BinaryExpression bin)
        {
            if (bin.IsDeclaration)
            {
                var declaredName = TryGetDeclaredName(bin.Left);
                var shouldEmitVar = true;
                if (bin.Left is IdentifierExpression id && id.Instance is IdentifierExpression instance &&
                    !instance.HideInstance)
                {
                    //this is to prevent adding `var` before `System.var = a;`
                    //do nothing
                    shouldEmitVar = false;
                }
                else
                {
                    if (_inForInitializer && !string.IsNullOrEmpty(declaredName) &&
                        _declaredLocals.Contains(declaredName))
                    {
                        shouldEmitVar = false;
                    }
                }

                if (shouldEmitVar)
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

            if (bin.IsDeclaration)
            {
                var declaredName = TryGetDeclaredName(bin.Left);
                if (!string.IsNullOrEmpty(declaredName))
                {
                    _declaredLocals.Add(declaredName);
                }
            }
        }

        private static string TryGetDeclaredName(Expression left)
        {
            switch (left)
            {
                case LocalExpression local:
                    return local.ToString();
                case IdentifierExpression id when id.Instance == null:
                    return id.Name;
                default:
                    return null;
            }
        }

        private static bool IsGlobalCtor(InvokeExpression invoke, string typeName)
        {
            if (invoke?.InvokeType != InvokeType.Ctor)
            {
                return false;
            }

            if (invoke.MethodExpression is IdentifierExpression id)
            {
                return id.FullName == $"global.{typeName}";
            }

            return false;
        }

        private bool TryWriteCollectionLiteralCtor(InvokeExpression invoke)
        {
            if (!Config.UseCollectionLiteralWhenPossible || invoke?.InvokeType != InvokeType.Ctor)
            {
                return false;
            }

            var isDictionary = IsGlobalCtor(invoke, "Dictionary");
            var isArray = IsGlobalCtor(invoke, "Array");
            if (!isDictionary && !isArray)
            {
                return false;
            }

            _formatter.WriteToken(isDictionary ? "%[" : "[");
            if (invoke.Parameters.Count <= 0)
            {
                _formatter.WriteToken("]");
                return true;
            }

            // TJS dictionary literal allows `=>` as readability-friendly pair separator.
            if (isDictionary && invoke.Parameters.Count % 2 == 0)
            {
                for (var i = 0; i < invoke.Parameters.Count; i += 2)
                {
                    Visit(invoke.Parameters[i]);
                    _formatter.WriteSpace();
                    _formatter.WriteToken("=>");
                    _formatter.WriteSpace();
                    Visit(invoke.Parameters[i + 1]);
                    if (i + 2 < invoke.Parameters.Count)
                    {
                        _formatter.WriteToken(",");
                        _formatter.WriteSpace();
                    }
                }

                _formatter.WriteToken("]");
                return true;
            }

            for (var i = 0; i < invoke.Parameters.Count; i++)
            {
                Visit(invoke.Parameters[i]);
                if (i < invoke.Parameters.Count - 1)
                {
                    _formatter.WriteToken(",");
                    _formatter.WriteSpace();
                }
            }

            _formatter.WriteToken("]");
            return true;
        }

        internal override void VisitInvokeExpr(InvokeExpression invoke)
        {
            if (invoke.InvokeType == InvokeType.RegExpCompile)
            {
                _formatter.WriteIdentifier(invoke.ToRegExp());
                return;
            }

            if (TryWriteCollectionLiteralCtor(invoke))
            {
                return;
            }

            if (invoke.InvokeType == InvokeType.Ctor)
            {
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
            // Skip Phi expressions that can be simplified (they should be inlined)
            if (expression.Expression is PhiExpression phi)
            {
                if (phi.CanSimplify || phi.PossibleExpressions.Count == 0)
                {
                    return;
                }
                
                // For unresolved Phi, output as comment only
                _formatter.Write("// Unresolved phi node (slot ");
                _formatter.Write(phi.Slot.ToString());
                _formatter.Write("): ");
                for (int i = 0; i < phi.PossibleExpressions.Count; i++)
                {
                    if (i > 0) _formatter.Write(" | ");
                    _formatter.Write(phi.PossibleExpressions[i]?.ToString() ?? "null");
                }
                _formatter.WriteLine();
                return;
            }

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
                    // Check if target is an unresolved Phi - if so, just use a placeholder
                    if (unary.Target is PhiExpression phi && !phi.CanSimplify && !phi.IsConditional)
                    {
                        // Output the target as a simple local reference instead of the phi
                        _formatter.WriteIdentifier("p" + Math.Abs(phi.Slot));
                    }
                    else
                    {
                        Visit(unary.Target);
                    }
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
            if (TryWriteContinueGuardIf(ifStmt))
            {
                AddNewLineAfterStructCtrlStmt();
                return;
            }

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

        private static bool IsEmptyStatement(Statement statement)
        {
            return statement switch
            {
                null => true,
                BlockStatement block => block.Statements == null || block.Statements.Count == 0,
                _ => false
            };
        }

        private static bool IsContinueStatement(Statement statement)
        {
            return statement switch
            {
                ContinueStatement => true,
                BlockStatement block when block.Statements?.Count == 1 && block.Statements[0] is ContinueStatement => true,
                _ => false
            };
        }

        private bool TryWriteContinueGuardIf(IfStatement ifStmt)
        {
            if (!IsEmptyStatement(ifStmt.Then) || ifStmt.Else is not IfStatement elseIf)
            {
                return false;
            }

            if (!IsContinueStatement(elseIf.Then) || elseIf.Else == null)
            {
                return false;
            }

            var mergedCondition = new BinaryExpression(ifStmt.Condition, elseIf.Condition, BinaryOp.LogicOr);
            _formatter.WriteKeyword("if");
            _formatter.WriteSpace();
            _formatter.WriteToken("(");
            Visit(mergedCondition);
            _formatter.WriteToken(")");
            _formatter.WriteLine();
            _formatter.WriteStartBlock();
            Visit(new ContinueStatement());
            _formatter.WriteEndBlock();
            _formatter.WriteKeyword("else");
            _formatter.WriteSpace();
            _formatter.WriteLine();
            _formatter.WriteStartBlock();
            Visit(elseIf.Else);
            _formatter.WriteEndBlock();
            return true;
        }

        internal override void VisitForStmt(ForStatement forStmt)
        {
            _formatter.WriteKeyword("for");
            _formatter.WriteSpace();
            _formatter.WriteToken("(");

            _inForInitializer = true;
            Visit(forStmt.Initializer);
            _inForInitializer = false;
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
            WriteLoopBody(forStmt.Body);
            _formatter.WriteEndBlock();
            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitDoWhileStmt(DoWhileStatement doWhile)
        {
            _formatter.WriteKeyword("do");
            _formatter.WriteLine();
            _formatter.WriteStartBlock();
            WriteLoopBody(doWhile.Body);
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
            WriteLoopBody(whileStmt.Body);
            _formatter.WriteEndBlock();
            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitPhiExpr(PhiExpression phi)
        {
            // If this is a conditional Phi (from if-else), output as ternary expression
            if (phi.IsConditional)
            {
                if (phi.Slot == Const.FlagReg && phi.Condition?.Condition != null)
                {
                    var cond = phi.Condition.Condition;
                    if (phi.ElseBranch != null && IsSameExpression(phi.ThenBranch, cond))
                    {
                        Visit(cond);
                        _formatter.WriteSpace();
                        _formatter.WriteToken("||");
                        _formatter.WriteSpace();
                        Visit(phi.ElseBranch);
                        return;
                    }

                    if (phi.ThenBranch != null && IsSameExpression(phi.ElseBranch, cond))
                    {
                        Visit(cond);
                        _formatter.WriteSpace();
                        _formatter.WriteToken("&&");
                        _formatter.WriteSpace();
                        Visit(phi.ThenBranch);
                        return;
                    }
                }

                _formatter.WriteToken("(");
                Visit(phi.Condition.Condition);
                _formatter.WriteSpace();
                _formatter.WriteToken("?");
                _formatter.WriteSpace();
                Visit(phi.ThenBranch);
                _formatter.WriteSpace();
                _formatter.WriteToken(":");
                _formatter.WriteSpace();
                Visit(phi.ElseBranch);
                _formatter.WriteToken(")");
            }
            // If simplified, output the single value
            else if (phi.CanSimplify)
            {
                Visit(phi.Simplify());
            }
            // If only one possible expression, use it
            else if (phi.PossibleExpressions.Count == 1)
            {
                Visit(phi.PossibleExpressions[0]);
            }
            // Otherwise, this is an unresolved Phi - try to output something reasonable
            else if (phi.PossibleExpressions.Count > 0)
            {
                // Try to find a common base and show differences
                // For now, just pick the first expression with a comment
                _formatter.Write("/*phi:");
                _formatter.Write(phi.Slot.ToString());
                _formatter.Write("*/ ");
                Visit(phi.PossibleExpressions[0]);
            }
            else
            {
                // Empty Phi - this shouldn't happen but handle it gracefully
                _formatter.Write("/*empty phi*/");
            }
        }

        private static bool IsSameExpression(Expression a, Expression b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            if (ReferenceEquals(a, b))
            {
                return true;
            }

            if (a is LocalExpression la && b is LocalExpression lb)
            {
                return la.Slot == lb.Slot;
            }

            if (a is IdentifierExpression ia && b is IdentifierExpression ib)
            {
                return ia.FullName == ib.FullName;
            }

            return a.ToString() == b.ToString();
        }

        internal override void VisitTryStmt(TryStatement tryStmt)
        {
            _formatter.WriteKeyword("try");
            _formatter.WriteLine();
            _formatter.WriteStartBlock();
            Visit(tryStmt.Try);
            _formatter.WriteEndBlock();

            if (tryStmt.Catch != null)
            {
                _formatter.WriteKeyword("catch");
                if (tryStmt.Catch.Expression is CatchExpression catchClause && catchClause.Exception != null)
                {
                    _formatter.WriteToken("(");
                    Visit(catchClause.Exception);
                    _formatter.WriteToken(")");
                }
                _formatter.WriteLine();
                _formatter.WriteStartBlock();
                // Catch body is temporarily stored in Finally
                if (tryStmt.Finally != null)
                {
                    Visit(tryStmt.Finally);
                }
                _formatter.WriteEndBlock();
            }

            AddNewLineAfterStructCtrlStmt();
        }
    }
}