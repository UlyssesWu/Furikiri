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
        private bool _inTopLevelBody;
        private CodeObject _currentClass;
        private string _currentClassSuperFullName;
        public Dictionary<Method, BlockStatement> MethodRefs = new Dictionary<Method, BlockStatement>();
        public Dictionary<Property, (BlockStatement Getter, BlockStatement Setter)> PropertyRefs =
            new Dictionary<Property, (BlockStatement Getter, BlockStatement Setter)>();
        public Dictionary<CodeObject, Expression> ClassSuperExpressions = new Dictionary<CodeObject, Expression>();

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

        public void WriteClass(CodeObject classObj)
        {
            if (classObj == null)
            {
                return;
            }

            var prevClass = _currentClass;
            var prevSuper = _currentClassSuperFullName;
            _currentClass = classObj;
            _currentClassSuperFullName = null;

            _formatter.WriteKeyword("class");
            _formatter.WriteSpace();
            _formatter.WriteIdentifier(classObj.Name);
            if (ClassSuperExpressions != null && ClassSuperExpressions.TryGetValue(classObj, out var superExpr) &&
                superExpr != null)
            {
                _currentClassSuperFullName = GetExpressionFullName(superExpr);
                _formatter.WriteSpace();
                _formatter.WriteKeyword("extends");
                _formatter.WriteSpace();
                Visit(superExpr);
            }

            _formatter.WriteLine();
            _formatter.WriteToken("{");
            _formatter.WriteLine();
            _formatter.Indent();

            var classMethods = MethodRefs
                .Where(m => m.Key.Object.Parent == classObj &&
                            m.Key.Object.ContextType == TjsContextType.Function &&
                            !m.Key.IsLambda)
                .OrderBy(m => m.Key.Name)
                .ToList();
            var constructor = classMethods.FirstOrDefault(m => m.Key.Name == classObj.Name);
            if (constructor.Key != null)
            {
                WriteClassMethod(constructor.Key, constructor.Value);
                _formatter.WriteLine();
            }

            foreach (var method in classMethods.Where(m => m.Key.Name != classObj.Name))
            {
                WriteClassMethod(method.Key, method.Value);
                _formatter.WriteLine();
            }

            foreach (var property in PropertyRefs.Where(p => p.Key.Parent == classObj))
            {
                WriteClassProperty(property.Key, property.Value.Getter, property.Value.Setter);
                _formatter.WriteLine();
            }

            _formatter.Outdent();
            _formatter.WriteToken("}");
            _formatter.WriteLine();

            _currentClass = prevClass;
            _currentClassSuperFullName = prevSuper;
        }

        private void WriteClassMethod(Method method, BlockStatement block)
        {
            _formatter.WriteKeyword("function");
            _formatter.WriteSpace();
            _formatter.WriteIdentifier(method.Name);
            _formatter.WriteToken("(");
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
            WriteMethodBody(block, true);
            _formatter.WriteLine();
        }

        private void WriteClassProperty(Property property, BlockStatement getterBlock, BlockStatement setterBlock)
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
            var prevInTopLevelBody = _inTopLevelBody;
            _inTopLevelBody = !braces;
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

            _inTopLevelBody = prevInTopLevelBody;
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
            if (IsClassAliasDeclaration(bin))
            {
                if (bin.IsDeclaration)
                {
                    var declaredName = TryGetDeclaredName(bin.Left);
                    if (!string.IsNullOrEmpty(declaredName))
                    {
                        _declaredLocals.Add(declaredName);
                    }
                }

                return;
            }

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

        private bool IsClassAliasDeclaration(BinaryExpression bin)
        {
            if (bin?.Op != BinaryOp.Assign || !bin.IsDeclaration)
            {
                return false;
            }

            if (bin.Right is not ConstantExpression constant || constant.Variant is not TjsCodeObject classObj ||
                classObj.Object?.ContextType != TjsContextType.Class)
            {
                return false;
            }

            var declaredName = TryGetDeclaredName(bin.Left);
            if (string.IsNullOrEmpty(declaredName) && bin.Left is IdentifierExpression idLeft)
            {
                declaredName = idLeft.Name;
            }
            if (string.IsNullOrEmpty(declaredName))
            {
                return false;
            }

            // Only suppress aliases for classes that are already emitted explicitly.
            return string.Equals(declaredName, classObj.Object.Name, StringComparison.Ordinal) &&
                   MethodRefs.Any(m => m.Key.Object.Parent == classObj.Object);
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

            if (TryWriteSuperInvoke(invoke))
            {
                return;
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
                // 输出展开运算符
                if (invoke.SpreadParameterIndices != null && invoke.SpreadParameterIndices.Contains(i))
                {
                    _formatter.WriteToken("*");
                }

                if (i < invoke.Parameters.Count - 1)
                {
                    _formatter.Write(", ");
                }
            }

            if (invoke.HasOmittedArguments)
            {
                if (invoke.Parameters.Count > 0)
                {
                    _formatter.Write(", ");
                }

                _formatter.Write("...");
            }

            _formatter.WriteToken(")");
        }

        private bool TryWriteSuperInvoke(InvokeExpression invoke)
        {
            if (_currentClass == null || string.IsNullOrEmpty(_currentClassSuperFullName))
            {
                return false;
            }

            if (invoke?.Instance is not IdentifierExpression id)
            {
                return false;
            }

            if (!IsSuperReference(id.FullName, _currentClassSuperFullName))
            {
                return false;
            }

            _formatter.WriteIdentifier("super");
            _formatter.WriteToken(".");
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
                Visit(invoke.Parameters[i]);
                if (i < invoke.Parameters.Count - 1)
                {
                    _formatter.Write(", ");
                }
            }

            if (invoke.HasOmittedArguments)
            {
                if (invoke.Parameters.Count > 0)
                {
                    _formatter.Write(", ");
                }

                _formatter.Write("...");
            }

            _formatter.WriteToken(")");
            return true;
        }

        private static bool IsSuperReference(string invokeInstanceName, string superName)
        {
            if (string.IsNullOrEmpty(invokeInstanceName) || string.IsNullOrEmpty(superName))
            {
                return false;
            }

            if (string.Equals(invokeInstanceName, superName, StringComparison.Ordinal))
            {
                return true;
            }

            return string.Equals(invokeInstanceName, $"global.{superName}", StringComparison.Ordinal);
        }

        private static string GetExpressionFullName(Expression expression)
        {
            return expression switch
            {
                IdentifierExpression id => id.FullName,
                LocalExpression local => local.ToString(),
                _ => expression?.ToString()
            };
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

        private static bool TryGetArrayCtorDeclaration(
            Statement statement,
            out LocalExpression target)
        {
            target = null;
            if (statement is not ExpressionStatement exprStmt ||
                exprStmt.Expression is not BinaryExpression bin ||
                !bin.IsDeclaration ||
                bin.Op != BinaryOp.Assign ||
                bin.Left is not LocalExpression local ||
                bin.Right is not InvokeExpression invoke ||
                !IsGlobalCtor(invoke, "Array"))
            {
                return false;
            }

            target = local;
            return true;
        }

        private static bool TryGetArrayItemAssignment(
            Statement statement,
            int targetSlot,
            out int index,
            out Expression value)
        {
            index = -1;
            value = null;
            if (statement is not ExpressionStatement exprStmt ||
                exprStmt.Expression is not BinaryExpression bin ||
                bin.Op != BinaryOp.Assign ||
                bin.Left is not PropertyAccessExpression access ||
                access.Instance is not LocalExpression instance ||
                instance.Slot != targetSlot ||
                access.Property is not ConstantExpression constant ||
                constant.Variant is not TjsInt intIndex ||
                intIndex.IntValue < 0)
            {
                return false;
            }

            index = intIndex.IntValue;
            value = bin.Right;
            return true;
        }

        private void WriteArrayLiteralDeclaration(LocalExpression target, List<Expression> values)
        {
            _formatter.WriteIdentifier("var");
            _formatter.WriteSpace();
            Visit(target);
            _formatter.WriteSpace();
            _formatter.WriteToken("=");
            _formatter.WriteSpace();
            _formatter.WriteToken("[");
            for (var i = 0; i < values.Count; i++)
            {
                Visit(values[i]);
                if (i < values.Count - 1)
                {
                    _formatter.WriteToken(",");
                    _formatter.WriteSpace();
                }
            }

            _formatter.WriteToken("]");
            _formatter.WriteToken(";");
            _formatter.WriteLine();
            _declaredLocals.Add(target.ToString());
        }

        internal override void VisitBlockStmt(BlockStatement block)
        {
            if (block?.Statements == null)
            {
                return;
            }

            for (var i = 0; i < block.Statements.Count; i++)
            {
                var statement = block.Statements[i] as Statement;
                if (Config.UseCollectionLiteralWhenPossible &&
                    statement != null &&
                    TryGetArrayCtorDeclaration(statement, out var target) &&
                    i + 1 < block.Statements.Count)
                {
                    var values = new List<Expression>();
                    var scan = i + 1;
                    while (scan < block.Statements.Count &&
                           block.Statements[scan] is Statement scanStatement &&
                           TryGetArrayItemAssignment(scanStatement, target.Slot, out var idx, out var value) &&
                           idx == values.Count)
                    {
                        values.Add(value);
                        scan++;
                    }

                    if (values.Count > 0)
                    {
                        WriteArrayLiteralDeclaration(target, values);
                        i = scan - 1;
                        continue;
                    }
                }

                Visit(block.Statements[i]);
            }
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
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    Visit(unary.Target);
                    break;
                case UnaryOp.Not:
                    // De Morgan 定律: 对条件标志 Phi 取反时，展开为 && 或 || 形式
                    if (unary.Target is PhiExpression notPhi && notPhi.IsConditional
                        && notPhi.Slot == Const.FlagReg && notPhi.Condition?.Condition != null)
                    {
                        var cond = notPhi.Condition.Condition;
                        // !(cond || elseBranch) => !cond && !elseBranch
                        if (notPhi.ElseBranch != null && IsSameExpression(notPhi.ThenBranch, cond))
                        {
                            _formatter.WriteToken("(");
                            VisitInverted(cond);
                            _formatter.WriteSpace();
                            _formatter.WriteToken("&&");
                            _formatter.WriteSpace();
                            VisitInverted(notPhi.ElseBranch);
                            _formatter.WriteToken(")");
                            break;
                        }

                        // !(cond && thenBranch) => !cond || !thenBranch
                        if (notPhi.ThenBranch != null && IsSameExpression(notPhi.ElseBranch, cond))
                        {
                            _formatter.WriteToken("(");
                            VisitInverted(cond);
                            _formatter.WriteSpace();
                            _formatter.WriteToken("||");
                            _formatter.WriteSpace();
                            VisitInverted(notPhi.ThenBranch);
                            _formatter.WriteToken(")");
                            break;
                        }
                    }

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
                case UnaryOp.PropertyRef:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    Visit(unary.Target);
                    break;
                case UnaryOp.PropertyObject:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    if (unary.Target is BinaryExpression)
                    {
                        _formatter.WriteToken("(");
                        Visit(unary.Target);
                        _formatter.WriteToken(")");
                    }
                    else
                    {
                        Visit(unary.Target);
                    }
                    break;
                case UnaryOp.Eval:
                    var targetOp = unary.Target as IOperation;
                    var oldSelfAssign = targetOp?.IsSelfAssignment ?? false;
                    if (targetOp != null)
                    {
                        targetOp.IsSelfAssignment = false;
                    }
                    _formatter.WriteToken("(");
                    Visit(unary.Target);
                    _formatter.WriteToken(")");
                    _formatter.WriteToken("!");
                    if (targetOp != null)
                    {
                        targetOp.IsSelfAssignment = oldSelfAssign;
                    }
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
            if (TryRewriteNoOpIfWithoutElse(ifStmt))
            {
                AddNewLineAfterStructCtrlStmt();
                return;
            }

            if (TryWriteNoOpThenElseRewrite(ifStmt))
            {
                AddNewLineAfterStructCtrlStmt();
                return;
            }

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

        private bool TryRewriteNoOpIfWithoutElse(IfStatement ifStmt)
        {
            if (ifStmt == null || ifStmt.IsElseIf || ifStmt.Else != null || !IsNoSideEffectBlock(ifStmt.Then))
            {
                return false;
            }

            // 当 then 和 else 都为空时，分支已被表达式传播阶段消耗（如三元表达式），
            // 条件中的副作用已在下游表达式中体现，无需重复输出
            if (ifStmt.Then == null)
            {
                return true;
            }

            var rawCond = ifStmt.Condition is ConditionExpression condWrap ? condWrap.Condition : ifStmt.Condition;
            if (ContainsShortCircuit(rawCond))
            {
                return false;
            }

            if (IsLikelyPureExpression(rawCond))
            {
                return true;
            }

            var effects = new List<Expression>();
            CollectSideEffects(rawCond, effects);
            if (effects.Count == 0)
            {
                // 条件与 then 都无副作用，整句可安全删除
                return true;
            }

            // 对于空 then 的 if，输出完整条件表达式，保持原有求值顺序与比较结构，
            // 避免把 `if (a() != "") {}` 退化成仅有 `a();` 的不直观形式。
            Visit(rawCond);
            _formatter.WriteToken(";");
            _formatter.WriteLine();

            return true;
        }

        private static bool IsLikelyPureExpression(Expression expression)
        {
            if (expression == null)
            {
                return true;
            }

            switch (expression)
            {
                case ConstantExpression:
                case IdentifierExpression:
                case LocalExpression:
                    return true;
                case UnaryExpression unary:
                    return IsLikelyPureExpression(unary.Target);
                case BinaryExpression bin when bin.Op != BinaryOp.Assign:
                    return IsLikelyPureExpression(bin.Left) && IsLikelyPureExpression(bin.Right);
                case ConditionExpression cond:
                    return IsLikelyPureExpression(cond.Condition);
                case PropertyAccessExpression prop:
                    return IsLikelyPureExpression(prop.Instance) && IsLikelyPureExpression(prop.Property);
                case InvokeExpression:
                    // Any function call may have side effects; never assume purity.
                    return false;
                default:
                    return false;
            }
        }

        private bool TryWriteNoOpThenElseRewrite(IfStatement ifStmt)
        {
            if (ifStmt?.Else == null || !IsNoSideEffectBlock(ifStmt.Then))
            {
                return false;
            }

            if (ifStmt.Else is not BlockStatement elseBlock || elseBlock.Statements == null || elseBlock.Statements.Count == 0)
            {
                return false;
            }

            var evalStatements = new List<IAstNode>();
            foreach (var statement in elseBlock.Statements)
            {
                if (statement is IfStatement nestedIf && IsPureNoOpIfStatement(nestedIf))
                {
                    continue;
                }

                if (statement is ExpressionStatement exprStmt && exprStmt.Expression != null && !HasSideEffect(exprStmt.Expression))
                {
                    continue;
                }

                if (IsPropertyEvalStatement(statement))
                {
                    evalStatements.Add(statement);
                    continue;
                }

                return false;
            }

            if (evalStatements.Count == 0)
            {
                return false;
            }

            foreach (var statement in evalStatements)
            {
                Visit(statement);
            }

            return true;
        }

        private static bool IsPureNoOpIfStatement(IfStatement ifStmt)
        {
            if (ifStmt == null || ifStmt.Else != null || !IsNoSideEffectBlock(ifStmt.Then))
            {
                return false;
            }

            var rawCond = ifStmt.Condition is ConditionExpression condWrap ? condWrap.Condition : ifStmt.Condition;
            return IsSideEffectFreeExpression(rawCond);
        }

        private static bool IsNoSideEffectBlock(Statement statement)
        {
            if (statement is not BlockStatement block || block.Statements == null || block.Statements.Count == 0)
            {
                return true;
            }

            foreach (var st in block.Statements)
            {
                if (st is not ExpressionStatement exprStmt || exprStmt.Expression == null)
                {
                    return false;
                }

                if (HasSideEffect(exprStmt.Expression))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasSideEffect(Expression expression)
        {
            return expression switch
            {
                InvokeExpression => true,
                BinaryExpression bin when bin.Op == BinaryOp.Assign => true,
                UnaryExpression unary when unary.Op is UnaryOp.Inc or UnaryOp.Dec or UnaryOp.Invalidate or UnaryOp.Eval => true,
                _ => false
            };
        }

        private static bool IsSideEffectFreeExpression(Expression expression)
        {
            if (expression == null)
            {
                return true;
            }

            return expression switch
            {
                ConstantExpression => true,
                IdentifierExpression => true,
                LocalExpression => true,
                UnaryExpression unary when unary.Op is UnaryOp.Not or UnaryOp.InvertSign or UnaryOp.ToInt or UnaryOp.ToReal or UnaryOp.ToString or UnaryOp.ToNumber or UnaryOp.ToByteArray
                    => IsSideEffectFreeExpression(unary.Target),
                BinaryExpression bin when bin.Op != BinaryOp.Assign
                    => IsSideEffectFreeExpression(bin.Left) && IsSideEffectFreeExpression(bin.Right),
                ConditionExpression cond => IsSideEffectFreeExpression(cond.Condition),
                _ => false
            };
        }

        private static bool ContainsShortCircuit(Expression expression)
        {
            if (expression == null)
            {
                return false;
            }

            return expression switch
            {
                BinaryExpression bin when bin.Op is BinaryOp.LogicAnd or BinaryOp.LogicOr => true,
                BinaryExpression bin => ContainsShortCircuit(bin.Left) || ContainsShortCircuit(bin.Right),
                UnaryExpression unary => ContainsShortCircuit(unary.Target),
                ConditionExpression cond => ContainsShortCircuit(cond.Condition),
                InvokeExpression invoke =>
                    ContainsShortCircuit(invoke.Instance) ||
                    ContainsShortCircuit(invoke.MethodExpression) ||
                    invoke.Parameters.Any(ContainsShortCircuit),
                PropertyAccessExpression prop =>
                    ContainsShortCircuit(prop.Instance) || ContainsShortCircuit(prop.Property),
                _ => false
            };
        }

        private static void CollectSideEffects(Expression expression, List<Expression> effects)
        {
            if (expression == null)
            {
                return;
            }

            switch (expression)
            {
                case InvokeExpression invoke:
                    effects.Add(invoke);
                    return;
                case UnaryExpression unary when unary.Op is UnaryOp.Inc or UnaryOp.Dec or UnaryOp.Invalidate or UnaryOp.Eval:
                    effects.Add(unary);
                    return;
                case BinaryExpression bin when bin.Op == BinaryOp.Assign:
                    effects.Add(bin);
                    return;
                case BinaryExpression bin:
                    CollectSideEffects(bin.Left, effects);
                    CollectSideEffects(bin.Right, effects);
                    return;
                case UnaryExpression unary:
                    CollectSideEffects(unary.Target, effects);
                    return;
                case ConditionExpression cond:
                    CollectSideEffects(cond.Condition, effects);
                    return;
                case PropertyAccessExpression prop:
                    CollectSideEffects(prop.Instance, effects);
                    CollectSideEffects(prop.Property, effects);
                    return;
            }
        }

        private static bool IsPropertyEvalStatement(IAstNode statement)
        {
            if (statement is not ExpressionStatement exprStmt ||
                exprStmt.Expression is not UnaryExpression unary ||
                unary.Op != UnaryOp.Eval)
            {
                return false;
            }

            return ContainsPropertyMarker(unary.Target);
        }

        private static bool ContainsPropertyMarker(Expression expression)
        {
            if (expression == null)
            {
                return false;
            }

            if (expression is ConstantExpression constant &&
                constant.Variant is TjsString str &&
                str.StringValue.Contains("property ", StringComparison.Ordinal))
            {
                return true;
            }

            if (expression is BinaryExpression bin)
            {
                return ContainsPropertyMarker(bin.Left) || ContainsPropertyMarker(bin.Right);
            }

            if (expression is UnaryExpression unary)
            {
                return ContainsPropertyMarker(unary.Target);
            }

            if (expression is InvokeExpression invoke)
            {
                if (ContainsPropertyMarker(invoke.MethodExpression))
                {
                    return true;
                }

                foreach (var parameter in invoke.Parameters)
                {
                    if (ContainsPropertyMarker(parameter))
                    {
                        return true;
                    }
                }
            }

            return false;
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
                        _formatter.WriteToken("(");
                        Visit(cond);
                        _formatter.WriteSpace();
                        _formatter.WriteToken("||");
                        _formatter.WriteSpace();
                        Visit(phi.ElseBranch);
                        _formatter.WriteToken(")");
                        return;
                    }

                    if (phi.ThenBranch != null && IsSameExpression(phi.ElseBranch, cond))
                    {
                        _formatter.WriteToken("(");
                        Visit(cond);
                        _formatter.WriteSpace();
                        _formatter.WriteToken("&&");
                        _formatter.WriteSpace();
                        Visit(phi.ThenBranch);
                        _formatter.WriteToken(")");
                        return;
                    }
                }

                _formatter.WriteToken("(");
                var ternaryCond = phi.Condition?.Condition;
                if (ternaryCond == null)
                {
                    _formatter.WriteKeyword("void");
                    _formatter.WriteSpace();
                    _formatter.WriteToken("?");
                    _formatter.WriteSpace();
                    Visit(phi.ThenBranch);
                    _formatter.WriteSpace();
                    _formatter.WriteToken(":");
                    _formatter.WriteSpace();
                    Visit(phi.ElseBranch);
                    _formatter.WriteToken(")");
                    return;
                }
                if (ternaryCond is BinaryExpression)
                {
                    _formatter.WriteToken("(");
                    Visit(ternaryCond);
                    _formatter.WriteToken(")");
                }
                else
                {
                    Visit(ternaryCond);
                }
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

        /// <summary>
        /// 输出表达式的逻辑取反形式，不修改原始 AST。
        /// 用于 De Morgan 变换时避免 mutate 共享节点。
        /// </summary>
        private void VisitInverted(Expression expr)
        {
            switch (expr)
            {
                case BinaryExpression binary:
                {
                    var invertedOp = binary.Op switch
                    {
                        BinaryOp.Equal => BinaryOp.NotEqual,
                        BinaryOp.NotEqual => BinaryOp.Equal,
                        BinaryOp.Congruent => BinaryOp.NotCongruent,
                        BinaryOp.NotCongruent => BinaryOp.Congruent,
                        BinaryOp.LessThan => BinaryOp.GreaterOrEqual,
                        BinaryOp.GreaterThan => BinaryOp.LessOrEqual,
                        BinaryOp.GreaterOrEqual => BinaryOp.LessThan,
                        BinaryOp.LessOrEqual => BinaryOp.GreaterThan,
                        _ => (BinaryOp?)null
                    };

                    if (invertedOp != null)
                    {
                        bool needBrackets = binary.NeedBrackets();
                        if (needBrackets) _formatter.WriteToken("(");
                        Visit(binary.Left);
                        _formatter.WriteSpace();
                        _formatter.WriteToken(invertedOp.Value.ToSymbol());
                        _formatter.WriteSpace();
                        Visit(binary.Right);
                        if (needBrackets) _formatter.WriteToken(")");
                    }
                    else
                    {
                        // 无法简单取反的二元运算，用 ! 包裹
                        _formatter.WriteToken("!");
                        Visit(binary);
                    }

                    break;
                }
                case UnaryExpression { Op: UnaryOp.Not } unary:
                    // 双重否定消除: !(!x) => x
                    Visit(unary.Target);
                    break;
                default:
                    _formatter.WriteToken("!");
                    Visit(expr);
                    break;
            }
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