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
        // 活跃性分析预计算的有效声明节点集合（null 表示未运行分析，使用旧的 _declaredLocals 兜底）
        private HashSet<BinaryExpression> _validDeclarations;
        private bool _inForInitializer;
        private bool _inTopLevelBody;
        // 已识别为默认参数的 if 语句集合，写函数体时跳过这些语句
        private readonly HashSet<IAstNode> _defaultParamStmtsToSkip = new HashSet<IAstNode>();
        private CodeObject _currentClass;
        private string _currentClassSuperFullName;
        public Dictionary<Method, BlockStatement> MethodRefs = new Dictionary<Method, BlockStatement>();
        public Dictionary<Property, (BlockStatement Getter, BlockStatement Setter)> PropertyRefs =
            new Dictionary<Property, (BlockStatement Getter, BlockStatement Setter)>();
        public Dictionary<CodeObject, Expression> ClassSuperExpressions = new Dictionary<CodeObject, Expression>();
        public Dictionary<CodeObject, BlockStatement> ClassBodies = new Dictionary<CodeObject, BlockStatement>();

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

        private void WriteSignature(Method method, Dictionary<string, Expression> defaults = null)
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
            var paramList = GetParameterList(method);
            WriteParamList(paramList, defaults ?? new Dictionary<string, Expression>());
            _formatter.WriteToken(")");
        }

        private static List<Variable> GetParameterList(Method method)
        {
            return method.Vars.Where(kv => kv.Value.IsParameter)
                .OrderByDescending(kv => kv.Key)
                .Select(kv => kv.Value)
                .ToList();
        }

        /// <summary>
        /// 从函数体开头提取形如 if (param === void) { param = defaultValue; } 的默认参数语句。
        /// 将识别出的语句加入 _defaultParamStmtsToSkip，返回参数名到默认值表达式的映射。
        /// </summary>
        private Dictionary<string, Expression> ExtractDefaultParams(List<Variable> paramList, BlockStatement block)
        {
            var defaults = new Dictionary<string, Expression>();
            _defaultParamStmtsToSkip.Clear();
            if (block?.Statements == null || paramList.Count == 0) return defaults;

            var paramNames = new HashSet<string>(paramList.Select(p => p.ToString()));

            foreach (var stmt in block.Statements)
            {
                if (stmt is IfStatement ifs && ifs.Else == null)
                {
                    // 解包 ConditionExpression（IfStatement.Condition 通常是 ConditionExpression 包装）
                    BinaryExpression cond = null;
                    if (ifs.Condition is ConditionExpression condExpr && condExpr.Condition is BinaryExpression condBin)
                        cond = condBin;
                    else if (ifs.Condition is BinaryExpression directBin)
                        cond = directBin;

                    if (cond == null || cond.Op != BinaryOp.Congruent) break;

                    // 检查条件左侧是否为参数变量
                    string paramName = null;
                    if (cond.Left is LocalExpression localLeft && paramNames.Contains(localLeft.Name))
                        paramName = localLeft.Name;
                    else if (cond.Left is IdentifierExpression idLeft && paramNames.Contains(idLeft.Name))
                        paramName = idLeft.Name;

                    if (paramName == null) break;

                    // 检查条件右侧是否为 void
                    if (!(cond.Right is ConstantExpression constRight && constRight.DataType == TjsVarType.Void))
                        break;

                    // 检查 then 分支是否为单条赋值：param = defaultValue
                    Expression defaultExpr = null;
                    if (ifs.Then is BlockStatement thenBlock && thenBlock.Statements?.Count == 1)
                        defaultExpr = TryExtractParamAssignment(thenBlock.Statements[0], paramName);
                    else if (ifs.Then is ExpressionStatement)
                        defaultExpr = TryExtractParamAssignment(ifs.Then, paramName);

                    if (defaultExpr == null) break;

                    defaults[paramName] = defaultExpr;
                    _defaultParamStmtsToSkip.Add(stmt);
                }
                else
                {
                    break;
                }
            }

            return defaults;
        }

        /// <summary>
        /// 尝试从节点中提取 param = value 赋值的右值，若匹配给定参数名则返回，否则返回 null。
        /// </summary>
        private static Expression TryExtractParamAssignment(IAstNode node, string paramName)
        {
            if (node is not ExpressionStatement es) return null;
            if (es.Expression is BinaryExpression assign && assign.Op == BinaryOp.Assign)
            {
                string lhsName = null;
                if (assign.Left is LocalExpression local) lhsName = local.Name;
                else if (assign.Left is IdentifierExpression id) lhsName = id.Name;
                if (lhsName == paramName) return assign.Right;
            }
            return null;
        }

        /// <summary>
        /// 将参数列表写入输出，支持默认值（param=value 语法）。
        /// </summary>
        private void WriteParamList(List<Variable> paramList, Dictionary<string, Expression> defaults)
        {
            if (paramList.Count == 0) return;
            for (int i = 0; i < paramList.Count - 1; i++)
            {
                _formatter.WriteIdentifier(paramList[i].ToString());
                if (paramList[i].IsNamedArray)
                    _formatter.WriteToken("*");
                if (defaults.TryGetValue(paramList[i].ToString(), out var defVal))
                {
                    _formatter.WriteToken("=");
                    Visit(defVal);
                }
                _formatter.WriteToken(",");
                _formatter.WriteSpace();
            }
            var last = paramList[paramList.Count - 1];
            _formatter.WriteIdentifier(last.ToString());
            if (last.IsNamedArray)
                _formatter.WriteToken("*");
            if (defaults.TryGetValue(last.ToString(), out var lastDef))
            {
                _formatter.WriteToken("=");
                Visit(lastDef);
            }
        }

        /// <summary>
        /// Write Function
        /// </summary>
        /// <param name="method"></param>
        /// <param name="block"></param>
        public void WriteFunction(Method method, BlockStatement block)
        {
            var paramList = GetParameterList(method);
            var defaults = ExtractDefaultParams(paramList, block);
            WriteSignature(method, defaults);
            WriteMethodBody(block, method.Object.ContextType != TjsContextType.TopLevel);
        }

        public void WriteProperty(Property property, BlockStatement getterBlock, BlockStatement setterBlock)
        {
            _formatter.WriteKeyword("property");
            _formatter.WriteSpace();
            _formatter.WriteIdentifier(property.Name);
            _formatter.WriteStartBlock();

            if (property.Setter != null && setterBlock != null)
            {
                var setterParams = GetParameterList(property.Setter);
                // setter 的形参语法不允许默认值。字节码中的
                // `if (value === void) value = defaultValue` 必须保留在函数体内。
                _defaultParamStmtsToSkip.Clear();
                _formatter.WriteKeyword("setter");
                _formatter.WriteToken("(");
                WriteParamList(setterParams, new Dictionary<string, Expression>());
                _formatter.WriteToken(")");
                WriteMethodBody(setterBlock, true);
                _formatter.WriteLine();
            }

            if (property.Getter != null && getterBlock != null)
            {
                _formatter.WriteKeyword("getter");
                _formatter.WriteToken("()");
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

            _formatter.WriteStartBlock();

            if (ClassBodies != null && ClassBodies.TryGetValue(classObj, out var classBody) &&
                classBody?.Statements != null && classBody.Statements.Count > 0)
            {
                WriteBlock(classBody);
                _formatter.WriteLine();
            }

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
            var paramList = GetParameterList(method);
            var defaults = ExtractDefaultParams(paramList, block);
            _formatter.WriteKeyword("function");
            _formatter.WriteSpace();
            _formatter.WriteIdentifier(method.Name);
            _formatter.WriteToken("(");
            WriteParamList(paramList, defaults);
            _formatter.WriteToken(")");
            WriteMethodBody(block, true);
            _formatter.WriteLine();
        }

        private void WriteClassProperty(Property property, BlockStatement getterBlock, BlockStatement setterBlock)
        {
            _formatter.WriteKeyword("property");
            _formatter.WriteSpace();
            _formatter.WriteIdentifier(property.Name);
            _formatter.WriteStartBlock();

            if (property.Setter != null && setterBlock != null)
            {
                var setterParams = GetParameterList(property.Setter);
                // TJS2 只允许普通函数参数使用 `arg=default`，setter 仍需输出
                // 原始的 void 检查，否则会生成让编译器崩溃的非法签名。
                _defaultParamStmtsToSkip.Clear();
                _formatter.WriteKeyword("setter");
                _formatter.WriteToken("(");
                WriteParamList(setterParams, new Dictionary<string, Expression>());
                _formatter.WriteToken(")");
                WriteMethodBody(setterBlock, true);
                _formatter.WriteLine();
            }

            if (property.Getter != null && getterBlock != null)
            {
                _formatter.WriteKeyword("getter");
                _formatter.WriteToken("()");
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
            var prevDeclaredLocals = new HashSet<string>(_declaredLocals);
            var prevValidDeclarations = _validDeclarations;
            _inTopLevelBody = !braces;
            _declaredLocals.Clear();

            // 预分析：通过活跃性启发式确定哪些声明节点真正需要 var
            _validDeclarations = block != null ? new DeclarationAnalyzer().Analyze(block) : null;

            if (braces)
            {
                _formatter.WriteStartBlock();
            }


            WriteBlock(block);

            if (braces)
            {
                _formatter.Outdent();
                //_formatter.WriteLine();
                _formatter.WriteToken("}");
            }

            _inTopLevelBody = prevInTopLevelBody;
            _validDeclarations = prevValidDeclarations;
            _declaredLocals.Clear();
            foreach (var declaredLocal in prevDeclaredLocals)
            {
                _declaredLocals.Add(declaredLocal);
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
            if (id.Instance != null && !id.HideInstance &&
                id.Instance is not IdentifierExpression &&
                id.Instance is not LocalExpression)
            {
                // FullName 只会展开简单的 a.b；成员前缀若是 links[i]、调用结果
                // 或其他复合表达式，必须递归写出，否则 links[i].object 会丢成 object。
                var needsParentheses = NeedsMemberAccessParentheses(id.Instance);
                if (needsParentheses) _formatter.WriteToken("(");
                Visit(id.Instance);
                if (needsParentheses) _formatter.WriteToken(")");
                _formatter.WriteToken(".");
                _formatter.WriteIdentifier(id.Name);
                return;
            }

            _formatter.WriteIdentifier(id.FullName);
        }

        internal override void VisitBinaryExpr(BinaryExpression bin)
        {
            if (IsClassAliasDeclaration(bin) || IsTopLevelMemberBinding(bin))
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

            var treatAsDeclaration = bin.IsDeclaration;

            if (treatAsDeclaration)
            {
                var declaredName = TryGetDeclaredName(bin.Left);
                var shouldEmitVar = true;

                if (_validDeclarations != null)
                {
                    // 使用活跃性预分析结果；for 初始化器中始终强制输出 var
                    shouldEmitVar = _validDeclarations.Contains(bin) || (_inForInitializer && bin.IsDeclaration);
                }
                else if (!string.IsNullOrEmpty(declaredName) && _declaredLocals.Contains(declaredName) && !_inForInitializer)
                {
                    // 兜底：旧的基于集合追踪的逻辑
                    shouldEmitVar = false;
                }

                if (bin.Left is IdentifierExpression id && id.Instance is IdentifierExpression instance &&
                    !instance.HideInstance)
                {
                    //this is to prevent adding `var` before `System.var = a;`
                    //do nothing
                    shouldEmitVar = false;
                }

                if (shouldEmitVar)
                {
                    _formatter.WriteIdentifier("var");
                    _formatter.WriteSpace();
                }
            }

            // 未初始化的类字段在字节码中以 SPDS member, void 表示。
            // 还原成 `var member;`，避免把实现层的 void 写回细节泄露到源码。
            if (_currentClass != null && treatAsDeclaration &&
                bin.Right is ConstantExpression voidValue && voidValue.DataType == TjsVarType.Void)
            {
                Visit(bin.Left);
                var classMemberName = TryGetDeclaredName(bin.Left);
                if (!string.IsNullOrEmpty(classMemberName))
                {
                    _declaredLocals.Add(classMemberName);
                }
                return;
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

            if (treatAsDeclaration)
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
                case IdentifierExpression id when !id.FullName.Contains("."):
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

        /// <summary>
        /// 检测顶层函数/属性成员绑定语句，即形如：
        ///   var NAME = (function)NAME incontextof this;
        ///   var NAME = (property)NAME incontextof this;
        /// 这类语句是 TJS2 字节码在顶层注册成员时产生的冗余语句，
        /// 对应的函数/属性在源码中已通过 function/property 关键字显式声明，无需重复输出。
        /// </summary>
        private bool IsTopLevelMemberBinding(BinaryExpression bin)
        {
            if (!_inTopLevelBody || bin?.Op != BinaryOp.Assign || !bin.IsDeclaration)
            {
                return false;
            }

            // 右侧必须是 xxx incontextof this
            if (bin.Right is not BinaryExpression ico || ico.Op != BinaryOp.InContextOf)
            {
                return false;
            }

            // incontextof 右侧必须是 this
            if (ico.Right is not IdentifierExpression thisId || thisId.IdentifierType != IdentifierType.This)
            {
                return false;
            }

            // incontextof 左侧必须是一个 TjsCodeObject 常量（函数或属性）
            if (ico.Left is not ConstantExpression codeObjConst || codeObjConst.Variant is not TjsCodeObject codeObj)
            {
                return false;
            }

            var ctxType = codeObj.Object?.ContextType;
            if (ctxType != TjsContextType.Function && ctxType != TjsContextType.Property)
            {
                return false;
            }

            // 被赋值的变量名必须与 CodeObject 名称一致
            var declaredName = TryGetDeclaredName(bin.Left);
            if (string.IsNullOrEmpty(declaredName) && bin.Left is IdentifierExpression idLeft)
            {
                declaredName = idLeft.Name;
            }
            if (string.IsNullOrEmpty(declaredName))
            {
                return false;
            }

            if (!string.Equals(declaredName, codeObj.Object.Name, StringComparison.Ordinal))
            {
                return false;
            }

            // 仅当对应函数或属性已在 MethodRefs/PropertyRefs 中登记时才隐藏
            bool alreadyDefined = MethodRefs.Any(m => m.Key.Object == codeObj.Object) ||
                                  PropertyRefs.Any(p => p.Key.Object == codeObj.Object);
            return alreadyDefined;
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

            var writeDictionaryAcrossLines = isDictionary &&
                                             invoke.Parameters.Count > 0 &&
                                             invoke.Parameters.Count % 2 == 0 &&
                                             ShouldWriteDictionaryAcrossLines(invoke.Parameters);

            _formatter.WriteToken(isDictionary ? "%[" : "[");
            if (invoke.Parameters.Count <= 0)
            {
                _formatter.WriteToken("]");
                return true;
            }

            // TJS dictionary literal allows `=>` as readability-friendly pair separator.
            if (isDictionary && invoke.Parameters.Count % 2 == 0)
            {
                if (writeDictionaryAcrossLines)
                {
                    _formatter.WriteLine();
                    _formatter.Indent();
                }

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
                        if (writeDictionaryAcrossLines)
                        {
                            _formatter.WriteLine();
                        }
                        else
                        {
                            _formatter.WriteSpace();
                        }
                    }
                }

                if (writeDictionaryAcrossLines)
                {
                    _formatter.WriteLine();
                    _formatter.Outdent();
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

        private bool ShouldWriteDictionaryAcrossLines(IReadOnlyList<Expression> parameters)
        {
            if (Config.MaxOutputLineLength <= 0)
            {
                return false;
            }

            // `%[`、`]`，以及每一对之间的 `, `。
            var estimatedLength = 3 + Math.Max(0, parameters.Count / 2 - 1) * 2;
            for (var i = 0; i < parameters.Count; i += 2)
            {
                estimatedLength += EstimateExpressionLength(parameters[i]);
                estimatedLength += 4; // ` => `
                estimatedLength += EstimateExpressionLength(parameters[i + 1]);
            }

            return _formatter.CurrentLineLength + estimatedLength > Config.MaxOutputLineLength;
        }

        /// <summary>
        /// 仅用于排版决策的保守长度估算。这里不能先把表达式真正写一遍再回滚，
        /// 因为写入过程还会更新局部变量声明等状态，重复访问可能改变最终输出。
        /// </summary>
        private static int EstimateExpressionLength(Expression expression)
        {
            if (expression == null)
            {
                return 0;
            }

            return expression switch
            {
                IdentifierExpression identifier => identifier.FullName?.Length ?? 0,
                LocalExpression local => local.ToString().Length,
                ConstantExpression constant => constant.ToString().Length,
                PropertyAccessExpression property =>
                    EstimateExpressionLength(property.Instance) +
                    EstimateExpressionLength(property.Property) + 2,
                BinaryExpression binary =>
                    EstimateExpressionLength(binary.Left) +
                    EstimateExpressionLength(binary.Right) +
                    binary.Op.ToSymbol().Length + 2 + (binary.NeedBrackets() ? 2 : 0),
                UnaryExpression unary =>
                    EstimateExpressionLength(unary.Target) + unary.Op.ToSymbol().Length,
                ConditionExpression condition => EstimateExpressionLength(condition.Condition),
                InvokeExpression call => EstimateInvokeLength(call),
                _ => Math.Max(1, expression.ToString()?.Length ?? 0)
            };
        }

        private static int EstimateInvokeLength(InvokeExpression invoke)
        {
            var length = invoke.Instance != null && !invoke.HideInstance
                ? EstimateExpressionLength(invoke.Instance) + 1
                : 0;
            length += invoke.MethodExpression != null
                ? EstimateExpressionLength(invoke.MethodExpression)
                : invoke.MethodName?.Length ?? 0;
            length += 2;
            for (var i = 0; i < invoke.Parameters.Count; i++)
            {
                length += EstimateExpressionLength(invoke.Parameters[i]);
                if (i + 1 < invoke.Parameters.Count)
                {
                    length += 2;
                }
            }

            return length;
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

            // 当调用目标是 incontextof 表达式时，需要加括号以确保正确的调用语义
            // 例如: (func incontextof obj)(args) 而非 func incontextof obj(args)
            bool needMethodParens = invoke.Instance == null &&
                                    invoke.MethodExpression is BinaryExpression binMethod &&
                                    binMethod.Op == BinaryOp.InContextOf;

            if (needMethodParens)
            {
                _formatter.WriteToken("(");
            }

            if (invoke.Instance != null && !invoke.HideInstance)
            {
                // 当实例本身是二元表达式（如 a + b）时，需要加括号以正确绑定方法调用
                // 例如: (System.exePath + lockKey).replace(...) 而非 System.exePath + lockKey.replace(...)
                bool instanceNeedsParens = NeedsMemberAccessParentheses(invoke.Instance);
                if (instanceNeedsParens)
                {
                    _formatter.WriteToken("(");
                }

                Visit(invoke.Instance);
                if (instanceNeedsParens)
                {
                    _formatter.WriteToken(")");
                }

                if (invoke.MethodExpression != null)
                {
                    // CALLI 以运行时表达式选择成员，TJS2 语法是 obj[expr](args)。
                    // 点号只能跟静态标识符，否则会生成 obj."prefix" + name(args)。
                    _formatter.WriteToken("[");
                    Visit(invoke.MethodExpression);
                    _formatter.WriteToken("]");
                }
                else
                {
                    _formatter.WriteToken(".");
                }
            }

            if (invoke.MethodExpression != null && (invoke.Instance == null || invoke.HideInstance))
            {
                Visit(invoke.MethodExpression);
            }
            else if (invoke.MethodExpression == null)
            {
                _formatter.WriteIdentifier(invoke.Method);
            }

            if (needMethodParens)
            {
                _formatter.WriteToken(")");
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
                var needsParentheses = NeedsMemberAccessParentheses(prop.Instance);
                if (needsParentheses) _formatter.WriteToken("(");
                Visit(prop.Instance);
                if (needsParentheses) _formatter.WriteToken(")");
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

        private static bool NeedsMemberAccessParentheses(Expression expression)
        {
            return expression is BinaryExpression or ConditionExpression ||
                   expression is InvokeExpression { InvokeType: InvokeType.Ctor };
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
                // 跳过已识别为默认参数的 if 语句（已写入函数签名）
                if (_defaultParamStmtsToSkip.Contains(block.Statements[i]))
                    continue;

                if (i > 0 && IsUnconditionalReturnNode(block.Statements[i - 1]) &&
                    IsUnconditionalReturnNode(block.Statements[i]))
                {
                    // 多个循环退出块可能汇合成相邻 return；第一个已终止执行，
                    // 后续 return 永远不可达，重复写出只会制造伪代码噪声。
                    continue;
                }

                if (i + 1 < block.Statements.Count &&
                    TryMergeGuardSelectorWithFollowingIf(
                        block.Statements[i], block.Statements[i + 1], out var guardedIf))
                {
                    Visit(guardedIf);
                    i++;
                    continue;
                }

                if (i + 1 < block.Statements.Count &&
                    TryMergeAdjacentLoopTransferIf(
                        block.Statements[i], block.Statements[i + 1], out var mergedTransferIf))
                {
                    Visit(mergedTransferIf);
                    i++;
                    continue;
                }

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

        private static bool IsUnconditionalReturnNode(IAstNode node)
        {
            return node is ReturnExpression ||
                   node is ExpressionStatement { Expression: ReturnExpression };
        }

        private static bool TryMergeGuardSelectorWithFollowingIf(
            IAstNode selectorNode, IAstNode followingNode, out IfStatement merged)
        {
            merged = null;
            var selector = selectorNode switch
            {
                ConditionExpression condition => condition.Condition,
                ExpressionStatement { Expression: ConditionExpression condition } => condition.Condition,
                _ => null
            };
            if (selector == null || !IsSideEffectFreeExpression(selector) ||
                followingNode is not IfStatement following || following.Else != null)
            {
                return false;
            }

            var followingCondition = UnwrapCondition(following.Condition);
            if (followingCondition is not BinaryExpression { Op: BinaryOp.LogicOr } disjunction)
            {
                return false;
            }

            var leftUsesSelector = ContainsConditionFactor(disjunction.Left, selector);
            var rightUsesSelector = ContainsConditionFactor(disjunction.Right, selector);
            if (leftUsesSelector == rightUsesSelector)
            {
                return false;
            }

            // CFG 中前一条件选择 OR 的两条求值臂，但共享 return 结构化后，
            // 选择条件可能作为裸表达式遗留。把未显式带 selector 的一臂补上
            // 反向 guard：`C; D || (C && E)` => `!C && D || C && E`。
            var guardedUniqueArm = selector.Invert().And(
                leftUsesSelector ? disjunction.Right : disjunction.Left);
            following.Condition = leftUsesSelector
                ? disjunction.Left.Or(guardedUniqueArm)
                : guardedUniqueArm.Or(disjunction.Right);
            merged = following;
            return true;
        }

        private static bool TryMergeAdjacentLoopTransferIf(
            IAstNode firstNode, IAstNode secondNode, out IfStatement merged)
        {
            merged = null;
            if (firstNode is not IfStatement first || secondNode is not IfStatement second ||
                first.Else != null || second.Else != null ||
                !TryGetSingleLoopTransfer(first.Then, out var firstTransfer) ||
                !TryGetSingleLoopTransfer(second.Then, out var secondTransfer) ||
                firstTransfer.GetType() != secondTransfer.GetType())
            {
                return false;
            }

            var firstCondition = first.Condition is ConditionExpression firstWrapper
                ? firstWrapper.Condition
                : first.Condition;
            var secondCondition = second.Condition is ConditionExpression secondWrapper
                ? secondWrapper.Condition
                : second.Condition;
            if (!IsSideEffectFreeExpression(firstCondition) ||
                !IsSideEffectFreeExpression(secondCondition))
            {
                return false;
            }

            // 连续的纯条件若都只执行同一种 break/continue，可保持短路求值并合并。
            // 这也是编译器拆分 `a || b` 循环守卫后的常见 CFG 形态。
            merged = new IfStatement(firstCondition.Or(secondCondition), first.Then, null);
            return true;
        }

        private static bool TryGetSingleLoopTransfer(Statement statement, out Statement transfer)
        {
            transfer = null;
            if (statement is BreakStatement or ContinueStatement)
            {
                transfer = statement;
                return true;
            }

            if (statement is not BlockStatement block || block.Statements.Count != 1 ||
                block.Statements[0] is not Statement nested ||
                nested is not BreakStatement && nested is not ContinueStatement)
            {
                return false;
            }

            transfer = nested;
            return true;
        }

        internal override void VisitLocalExpr(LocalExpression local)
        {
            _formatter.WriteIdentifier(local.ToString());
        }

        internal override void VisitDeleteExpr(DeleteExpression delete)
        {
            _formatter.WriteKeyword("delete");
            _formatter.WriteSpace();

            // DELI 的成员名是运行时表达式，必须使用 obj[index]；点号只适用于
            // DELD 的静态标识符。直接 ToString 会产生 obj.(int) value 等非法语法。
            if (delete.IdentifierExpression != null)
            {
                if (delete.Instance != null && !delete.HideInstance)
                {
                    Visit(delete.Instance);
                }
                _formatter.WriteToken("[");
                Visit(delete.IdentifierExpression);
                _formatter.WriteToken("]");
                return;
            }

            if (delete.Instance != null && !delete.HideInstance)
            {
                Visit(delete.Instance);
                _formatter.WriteToken(".");
            }

            _formatter.WriteIdentifier(delete.Identifier);
        }

        internal override void VisitUnaryExpr(UnaryExpression unary)
        {
            // Invalidate 是独立语句操作（invalidate target），
            // 不应套用自赋值前缀（target = invalidate target 是语义错误）。
            if (unary.Op == UnaryOp.Invalidate)
            {
                _formatter.WriteToken("invalidate");
                _formatter.WriteSpace();
                Visit(unary.Target);
                return;
            }

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
                    if (unary.IsPrefix)
                    {
                        // 前置运算符：++x / --x（对应 INCPD/DECPD 结果寄存器被下游使用）
                        _formatter.WriteToken(unary.Op.ToSymbol());
                        Visit(unary.Target);
                    }
                    else
                    {
                        Visit(unary.Target);
                        _formatter.WriteToken(unary.Op.ToSymbol());
                    }
                    break;
                case UnaryOp.BitNot:
                case UnaryOp.InvertSign:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    // BinaryExpression 会根据一元父节点自行加括号，最终得到
                    // `~(A | B)`；这里不能再套一层，否则会产生多余的双括号。
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
                    // 检查目标是否是无法化简的 Phi，若是则输出占位符标识符
                    if (unary.Target is PhiExpression phi && !phi.CanSimplify && !phi.IsConditional)
                    {
                        // FlagReg = Int32.MinValue，Math.Abs 会溢出，需做特殊处理
                        var slotId = phi.Slot == Const.FlagReg ? "flag" : Math.Abs(phi.Slot).ToString();
                        _formatter.WriteIdentifier("p" + slotId);
                    }
                    else
                    {
                        Visit(unary.Target);
                    }
                    break;
                case UnaryOp.ToNumber:
                    // TJS2 的一元 + 运算符：将值转换为数值类型
                    _formatter.WriteToken("+");
                    Visit(unary.Target);
                    break;
                case UnaryOp.ToInt:
                    // TJS2 的 int() 函数调用：转换为整数
                    _formatter.WriteToken("int");
                    _formatter.WriteToken("(");
                    Visit(unary.Target);
                    _formatter.WriteToken(")");
                    break;
                case UnaryOp.ToReal:
                    // TJS2 的 real() 函数调用：转换为实数
                    _formatter.WriteToken("real");
                    _formatter.WriteToken("(");
                    Visit(unary.Target);
                    _formatter.WriteToken(")");
                    break;
                case UnaryOp.ToString:
                    // TJS2 的 string() 函数调用：转换为字符串
                    _formatter.WriteToken("string");
                    _formatter.WriteToken("(");
                    Visit(unary.Target);
                    _formatter.WriteToken(")");
                    break;
                case UnaryOp.ToByteArray:
                    // TJS2 的 octet() 函数调用：转换为字节数组
                    _formatter.WriteToken("octet");
                    _formatter.WriteToken("(");
                    Visit(unary.Target);
                    _formatter.WriteToken(")");
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
                case UnaryOp.IsValid:
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
            TryFlattenSharedElseBranch(ifStmt);

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
            _formatter.WriteStartBlock();
            var savedHideVoidReturn = HideVoidReturn;
            HideVoidReturn = false;
            Visit(ifStmt.Then);
            _formatter.WriteEndBlock();
            if (ifStmt.Else != null)
            {
                _formatter.WriteKeyword("else");
                if (ifStmt.Else is IfStatement elseIf && elseIf.IsElseIf)
                {
                    _formatter.WriteSpace();
                    VisitIfStmt(elseIf);
                }
                else
                {
                    _formatter.WriteStartBlock();
                    Visit(ifStmt.Else);
                    _formatter.WriteEndBlock();
                }
            }
            HideVoidReturn = savedHideVoidReturn;

            AddNewLineAfterStructCtrlStmt();
        }

        private static bool TryFlattenSharedElseBranch(IfStatement outer)
        {
            var inner = UnwrapSingleIf(outer?.Then);
            var emptyOuterElse = UnwrapSingleIf(outer?.Else);
            var sharedElse = UnwrapSingleIf(inner?.Else);
            if (inner == null || emptyOuterElse == null || sharedElse == null ||
                !IsStructuralNoOpIfStatement(emptyOuterElse) ||
                IsStructuralNoOpIfStatement(sharedElse))
            {
                return false;
            }

            var emptyCondition = UnwrapCondition(emptyOuterElse.Condition);
            var sharedCondition = UnwrapCondition(sharedElse.Condition);
            if (!ContainsConditionFactor(sharedCondition, emptyCondition))
            {
                return false;
            }

            // CFG 共享形态：C ? (N ? A : B) : B。内层先结构化时 B 只挂在
            // C 的 then 下，并在 outer else 留下空壳。恢复成 (C && N) ? A : B。
            outer.Condition = UnwrapCondition(outer.Condition).And(UnwrapCondition(inner.Condition));
            outer.Then = inner.Then;
            outer.Else = inner.Else;
            if (outer.Else is IfStatement elseIf)
            {
                elseIf.IsElseIf = true;
            }
            return true;
        }

        private static IfStatement UnwrapSingleIf(Statement statement)
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

        private static Expression UnwrapCondition(Expression expression)
        {
            return expression is ConditionExpression wrapper ? wrapper.Condition : expression;
        }

        private static bool ContainsConditionFactor(Expression expression, Expression factor)
        {
            if (AreEquivalentConditionExpression(expression, factor))
            {
                return true;
            }

            return expression is BinaryExpression binary &&
                   binary.Op is BinaryOp.LogicAnd or BinaryOp.LogicOr &&
                   (ContainsConditionFactor(binary.Left, factor) ||
                    ContainsConditionFactor(binary.Right, factor));
        }

        private static bool AreEquivalentConditionExpression(Expression left, Expression right)
        {
            left = UnwrapCondition(left);
            right = UnwrapCondition(right);
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            return (left, right) switch
            {
                (LocalExpression l, LocalExpression r) => l.Slot == r.Slot,
                (IdentifierExpression l, IdentifierExpression r) => l.FullName == r.FullName,
                (ConstantExpression l, ConstantExpression r) => l.ToString() == r.ToString(),
                (UnaryExpression l, UnaryExpression r) => l.Op == r.Op &&
                                                         AreEquivalentConditionExpression(l.Target, r.Target),
                (BinaryExpression l, BinaryExpression r) => l.Op == r.Op &&
                    AreEquivalentConditionExpression(l.Left, r.Left) &&
                    AreEquivalentConditionExpression(l.Right, r.Right),
                _ => false
            };
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
                case BinaryExpression bin when bin.IsSelfAssignment:
                    return false;
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

        /// <summary>
        /// 结构性 no-op 判断：只要 then/else 块均无副作用即可，不要求条件本身无副作用。
        /// 用于识别短路条件产生的、结果已被内联消费的嵌套 IfStatement 占位结构。
        /// </summary>
        private static bool IsStructuralNoOpIfStatement(IfStatement ifStmt)
        {
            if (ifStmt == null)
            {
                return false;
            }

            if (!IsNoSideEffectBlock(ifStmt.Then))
            {
                return false;
            }

            if (ifStmt.Else != null && !IsNoSideEffectBlock(ifStmt.Else))
            {
                return false;
            }

            return true;
        }

        private static bool IsNoSideEffectBlock(Statement statement)
        {
            // ContinueStatement、BreakStatement 等控制流语句有实际作用，不视为无副作用
            if (statement is ContinueStatement or BreakStatement)
            {
                return false;
            }

            if (statement is not BlockStatement block || block.Statements == null || block.Statements.Count == 0)
            {
                return true;
            }

            foreach (var st in block.Statements)
            {
                // 嵌套 if 语句：只要 then/else 块均无副作用则视为无副作用（短路条件可能含函数调用，
                // 但其结果已被内联到下游表达式，此处作为结构性占位，无需输出）
                if (st is IfStatement nestedIf)
                {
                    if (!IsStructuralNoOpIfStatement(nestedIf))
                    {
                        return false;
                    }
                    continue;
                }

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
                ReturnExpression => true,
                ThrowExpression => true,
                // delete 即使目标成员不存在也属于可观察的对象写操作，不能把
                // 只含 DELD/DELI 的条件分支判为结构性空壳。
                DeleteExpression => true,
                // 命名寄存器上的 ADD/SUB 等指令在 AST 写出时才会补上
                // IsSelfAssignment；无副作用分析发生得更早，必须按运算符识别，
                // 否则包含 `x += y` 的整个嵌套 if 会被当成空壳删除。
                BinaryExpression bin when bin.IsSelfAssignment || bin.Op == BinaryOp.Assign ||
                                          bin.Op.CanSelfAssign() => true,
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
                BinaryExpression bin when !bin.IsSelfAssignment && bin.Op != BinaryOp.Assign
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
            _formatter.WriteStartBlock();
            Visit(new ContinueStatement());
            _formatter.WriteEndBlock();
            _formatter.WriteKeyword("else");
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
            _formatter.WriteStartBlock();
            WriteLoopBody(forStmt.Body);
            _formatter.WriteEndBlock();
            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitDoWhileStmt(DoWhileStatement doWhile)
        {
            _formatter.WriteKeyword("do");
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
            _formatter.WriteStartBlock();
            WriteLoopBody(whileStmt.Body);
            _formatter.WriteEndBlock();
            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitPhiExpr(PhiExpression phi)
        {
            if (phi.IsConditional && IsSameExpression(phi.ThenBranch, phi.ElseBranch))
            {
                Visit(phi.ThenBranch);
                return;
            }

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
            _formatter.WriteStartBlock();
            // 活跃性预分析已正确处理 try 块中的变量声明，无需重置 _declaredLocals
            Visit(tryStmt.Try);
            _formatter.WriteEndBlock();

            if (tryStmt.Catch != null)
            {
                _formatter.WriteKeyword("catch");
                IAstNode catchVariableCopy = null;
                Expression catchParameter = null;
                if (tryStmt.Catch.Expression is CatchExpression catchClause && catchClause.Exception != null)
                {
                    var catchBody = tryStmt.Finally;
                    catchParameter = catchClause.Exception;
                    // VM 先把隐式异常寄存器复制到源码 catch 变量。若删除这条
                    // 人工赋值，就必须把赋值左侧写成 catch 参数，否则正文中的
                    // e.message 会退化成未定义的 vN。
                    if (TryGetCatchVariableCopy(
                            catchBody, catchClause.Exception,
                            out var copiedParameter, out catchVariableCopy))
                    {
                        catchParameter = copiedParameter;
                    }

                    // 仅当 catch 块实际使用异常变量时输出参数。
                    bool exceptionIsUsed = catchBody != null &&
                                           CatchBodyReferencesExpression(
                                               catchBody, catchParameter, catchVariableCopy);
                    if (exceptionIsUsed)
                    {
                        _formatter.WriteToken("(");
                        Visit(catchParameter);
                        _formatter.WriteToken(")");
                    }
                }
                _formatter.WriteStartBlock();
                // Catch body is temporarily stored in Finally
                if (tryStmt.Finally != null)
                {
                    foreach (var node in tryStmt.Finally.Statements)
                    {
                        if (ReferenceEquals(node, catchVariableCopy))
                            continue;
                        Visit(node);
                    }
                }
                _formatter.WriteEndBlock();
            }

            AddNewLineAfterStructCtrlStmt();
        }

        /// <summary>
        /// 检查 catch 块的语句中是否实际引用了指定的表达式对象（引用相等），忽略平凡拷贝
        /// </summary>
        private static bool CatchBodyReferencesExpression(
            BlockStatement block, Expression target, IAstNode ignoredNode = null)
        {
            if (block?.Statements == null || target == null) return false;
            return block.Statements.Any(stmt =>
                !ReferenceEquals(stmt, ignoredNode) && AstNodeReferencesExpression(stmt, target));
        }

        private static bool TryGetCatchVariableCopy(
            BlockStatement block, Expression implicitException,
            out Expression parameter, out IAstNode copyNode)
        {
            parameter = null;
            copyNode = null;
            if (block?.Statements == null)
            {
                return false;
            }

            foreach (var node in block.Statements)
            {
                if (node is ExpressionStatement exprStmt &&
                    exprStmt.Expression is BinaryExpression bin &&
                    bin.Op == BinaryOp.Assign &&
                    AreSameCatchVariable(bin.Right, implicitException))
                {
                    parameter = bin.Left;
                    copyNode = node;
                    return true;
                }
            }

            return false;
        }

        private static bool AreSameCatchVariable(Expression left, Expression right)
        {
            return ReferenceEquals(left, right) ||
                   left is LocalExpression local && right is LocalExpression targetLocal &&
                   local.Slot == targetLocal.Slot ||
                   left is IdentifierExpression identifier && right is IdentifierExpression targetIdentifier &&
                   identifier.FullName == targetIdentifier.FullName;
        }

        private static bool AstNodeReferencesExpression(IAstNode node, Expression target)
        {
            if (node == null) return false;
            if (node is Expression expression && AreSameCatchVariable(expression, target))
            {
                return true;
            }
            return node switch
            {
                BlockStatement block =>
                    block.Statements?.Any(s => AstNodeReferencesExpression(s, target)) ?? false,
                ExpressionStatement exprStmt =>
                    AstNodeReferencesExpression(exprStmt.Expression, target),
                IfStatement ifStmt =>
                    AstNodeReferencesExpression(ifStmt.Condition, target) ||
                    AstNodeReferencesExpression(ifStmt.Then, target) ||
                    AstNodeReferencesExpression(ifStmt.Else, target),
                BinaryExpression bin =>
                    AstNodeReferencesExpression(bin.Left, target) ||
                    AstNodeReferencesExpression(bin.Right, target),
                UnaryExpression unary =>
                    AstNodeReferencesExpression(unary.Target, target),
                IdentifierExpression identifier =>
                    AstNodeReferencesExpression(identifier.Instance, target),
                PropertyAccessExpression property =>
                    AstNodeReferencesExpression(property.Instance, target) ||
                    AstNodeReferencesExpression(property.Property, target),
                InvokeExpression invoke =>
                    AstNodeReferencesExpression(invoke.Instance, target) ||
                    AstNodeReferencesExpression(invoke.MethodExpression, target) ||
                    (invoke.Parameters?.Any(p => AstNodeReferencesExpression(p, target)) ?? false),
                ReturnExpression ret =>
                    AstNodeReferencesExpression(ret.Return, target),
                TryStatement tryInner =>
                    AstNodeReferencesExpression(tryInner.Try, target),
                _ => false
            };
        }

        /// <summary>
        /// 确定哪些 IsDeclaration=true 的赋值节点需要输出 var。VM 的 CP 指令不区分
        /// 声明与普通赋值，因此同一函数槽位只在首次定义时声明；后续写入即使经过
        /// 读取也仍是赋值。这样可避免循环或 switch 内把 `flag = false` 错写成第二个 var。
        /// </summary>
        private sealed class DeclarationAnalyzer
        {
            // 需要输出 var 的节点集合（按引用相等）
            private readonly HashSet<BinaryExpression> _validDeclarations =
                new HashSet<BinaryExpression>(ReferenceEqualityComparer.Instance);

            // 已出现的函数局部名；TJS2 的 var 是函数局部声明，无需按块重复输出。
            private readonly Dictionary<string, bool> _liveAfterDef =
                new Dictionary<string, bool>();

            internal HashSet<BinaryExpression> Analyze(BlockStatement body)
            {
                Walk(body);
                return _validDeclarations;
            }

            private void MarkRead(string name)
            {
                if (name != null)
                    _liveAfterDef[name] = true;
            }

            private void ProcessDefinition(BinaryExpression bin, string name)
            {
                if (!_liveAfterDef.ContainsKey(name))
                {
                    // 首次定义：加入有效声明集合
                    _validDeclarations.Add(bin);
                }

                // 后续读取信息仍供遍历使用，但不再把同槽位写入提升成新声明。
                _liveAfterDef[name] = false;
            }

            private void Walk(IAstNode node)
            {
                switch (node)
                {
                    case null:
                        return;
                    case BlockStatement block:
                        if (block.Statements != null)
                            foreach (var stmt in block.Statements)
                                Walk(stmt);
                        break;
                    case ExpressionStatement exprStmt:
                        Walk(exprStmt.Expression);
                        break;
                    case BinaryExpression bin:
                        WalkBinary(bin);
                        break;
                    case LocalExpression local:
                        // 读取上下文：标记为活跃
                        MarkRead(local.ToString());
                        break;
                    case IdentifierExpression idExpr:
                        // 简单标识符读取
                        if (!string.IsNullOrEmpty(idExpr.Name))
                        {
                            if (idExpr.Instance == null || !idExpr.FullName.Contains("."))
                                MarkRead(idExpr.Name);
                        }
                        break;
                    case IfStatement ifStmt:
                        Walk(ifStmt.Condition);
                        Walk(ifStmt.Then);
                        Walk(ifStmt.Else);
                        break;
                    case ForStatement forStmt:
                        Walk(forStmt.Initializer);
                        Walk(forStmt.Condition);
                        Walk(forStmt.Body);
                        Walk(forStmt.Increment);
                        break;
                    case WhileStatement whileStmt:
                        Walk(whileStmt.Condition);
                        Walk(whileStmt.Body);
                        break;
                    case DoWhileStatement doWhile:
                        Walk(doWhile.Body);
                        Walk(doWhile.Condition);
                        break;
                    case TryStatement tryStmt:
                        Walk(tryStmt.Try);
                        Walk(tryStmt.Finally); // catch 块内容存于 Finally
                        break;
                    case ReturnExpression ret:
                        Walk(ret.Return);
                        break;
                    case UnaryExpression unary:
                        // INC/DEC 等自赋值：读取目标（标记活跃），不作为新声明
                        Walk(unary.Target);
                        break;
                    case InvokeExpression invoke:
                        Walk(invoke.Instance);
                        Walk(invoke.MethodExpression);
                        if (invoke.Parameters != null)
                            foreach (var p in invoke.Parameters)
                                Walk(p);
                        break;
                    case ConditionExpression cond:
                        Walk(cond.Condition);
                        break;
                    case PhiExpression phi:
                        Walk(phi.Condition?.Condition);
                        Walk(phi.ThenBranch);
                        Walk(phi.ElseBranch);
                        if (phi.PossibleExpressions != null)
                            foreach (var e in phi.PossibleExpressions)
                                Walk(e);
                        break;
                    case PropertyAccessExpression prop:
                        Walk(prop.Instance);
                        break;
                    case ThrowExpression throwExpr:
                        Walk(throwExpr.Target);
                        break;
                    case DeleteExpression del:
                        Walk(del.Instance);
                        break;
                    case ConstantExpression _:
                        // Lambda/闭包拥有独立作用域，不向下分析
                        break;
                    case CatchExpression _:
                        // catch 变量属于写入目标，不作为读取处理
                        break;
                    // BreakStatement、ContinueStatement 无子节点，不处理
                }
            }

            private void WalkBinary(BinaryExpression bin)
            {
                if (bin.IsDeclaration && !bin.IsSelfAssignment)
                {
                    // 右侧为读取上下文
                    Walk(bin.Right);
                    // 左侧为定义目标
                    var name = TryGetDeclaredName(bin.Left);
                    if (!string.IsNullOrEmpty(name))
                    {
                        ProcessDefinition(bin, name);
                    }
                    else
                    {
                        // 非简单局部变量目标（如 arr[0] = ...），左侧按读取处理
                        Walk(bin.Left);
                    }
                }
                else if (bin.IsSelfAssignment)
                {
                    // 自赋值（v4 += x 等）：双侧均读取，但不产生新声明
                    Walk(bin.Left);
                    Walk(bin.Right);
                }
                else
                {
                    // 普通二元运算：双侧均为读取
                    Walk(bin.Left);
                    Walk(bin.Right);
                }
            }
        }
    }
}
