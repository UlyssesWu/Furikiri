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
        private IReadOnlyCollection<string> _hoistedLocalDeclarations = Array.Empty<string>();
        private bool _inForInitializer;
        private bool _inTopLevelBody;
        private BlockStatement _topLevelRootBlock;
        private bool _insideRecoveredTopLevelScope;
        // 只能省略当前函数体最末尾的隐式 return。catch、循环或普通分支中的
        // 无值 return 都承担控制流语义，不能按表达式类型全局过滤。
        private ReturnExpression _terminalVoidReturnToHide;
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
        /// Do not write the terminal implicit return if there is nothing to return
        /// </summary>
        public bool HideVoidReturn { get; set; } = Config.HideVoidReturn;

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
                    _formatter.WriteSpace();
                    _formatter.WriteToken("=");
                    _formatter.WriteSpace();
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
                _formatter.WriteSpace();
                _formatter.WriteToken("=");
                _formatter.WriteSpace();
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
                if (WriteClassInitializerBody(classBody))
                {
                    _formatter.WriteLine();
                }
            }

            // 类 CodeObject 的 Parent 记录了源码级嵌套关系。嵌套类必须留在当前
            // 大括号内，否则 `Outer.Inner` 会被提升成无关的全局类。
            var nestedClasses = ClassBodies?.Keys
                .Where(candidate => candidate.Parent == classObj)
                .ToList() ?? new List<CodeObject>();
            foreach (var nestedClass in nestedClasses)
            {
                WriteClass(nestedClass);
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

        /// <summary>
        /// 写出类初始化器中真正的字段初始化语句，并省略 VM 自动附加的末尾无值返回。
        /// </summary>
        private bool WriteClassInitializerBody(BlockStatement block)
        {
            var previousTopLevel = _inTopLevelBody;
            var previousDeclarations = new HashSet<string>(_declaredLocals);
            var previousValidDeclarations = _validDeclarations;
            var previousHoistedDeclarations = _hoistedLocalDeclarations;
            var previousTerminalReturn = _terminalVoidReturnToHide;
            _inTopLevelBody = false;
            _declaredLocals.Clear();
            _validDeclarations = new LocalDeclarationAnalysis().Analyze(block, false);
            _hoistedLocalDeclarations = Array.Empty<string>();
            _terminalVoidReturnToHide = null;

            if (HideVoidReturn && block?.Statements?.Count > 0 &&
                TryGetReturnExpression(block.Statements[^1]) is
                    { Return: null } terminalVoidReturn)
            {
                // 类方法和属性稍后由对象关系单独写入，因此这个 return 即使在
                // 最终文本中位于成员声明之前，仍是类初始化字节码的隐式尾部。
                _terminalVoidReturnToHide = terminalVoidReturn;
            }

            var hasVisibleStatement = block?.Statements?.Any(statement =>
                !ReferenceEquals(TryGetReturnExpression(statement),
                    _terminalVoidReturnToHide)) == true;
            WriteBlock(block);

            _inTopLevelBody = previousTopLevel;
            _validDeclarations = previousValidDeclarations;
            _hoistedLocalDeclarations = previousHoistedDeclarations;
            _terminalVoidReturnToHide = previousTerminalReturn;
            _declaredLocals.Clear();
            foreach (var declaration in previousDeclarations)
            {
                _declaredLocals.Add(declaration);
            }

            return hasVisibleStatement;
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
            var prevHoistedDeclarations = _hoistedLocalDeclarations;
            var prevTerminalVoidReturnToHide = _terminalVoidReturnToHide;
            var prevTopLevelRootBlock = _topLevelRootBlock;
            _inTopLevelBody = !braces;
            _topLevelRootBlock = !braces ? block : null;
            _declaredLocals.Clear();

            _terminalVoidReturnToHide = null;
            if (HideVoidReturn && block?.Statements?.Count > 0 &&
                TryGetReturnExpression(block.Statements[^1]) is
                    { Return: null } terminalVoidReturn)
            {
                _terminalVoidReturnToHide = terminalVoidReturn;
            }

            // 先恢复词法声明。若同一 VM 槽在多个结构化区域重复承担局部变量，
            // 将其提升到方法入口，避免 if/switch 改写后丢失原来的块边界。
            var declarationAnalyzer = block != null ? new LocalDeclarationAnalysis() : null;
            _validDeclarations = declarationAnalyzer?.Analyze(block, braces);
            _hoistedLocalDeclarations = braces
                ? declarationAnalyzer?.HoistedDeclarations ?? Array.Empty<string>()
                : Array.Empty<string>();

            if (braces)
            {
                _formatter.WriteStartBlock();
            }

            foreach (var name in _hoistedLocalDeclarations)
            {
                _formatter.WriteKeyword("var");
                _formatter.WriteSpace();
                _formatter.WriteIdentifier(name);
                _formatter.WriteToken(";");
                _formatter.WriteLine();
                _declaredLocals.Add(name);
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
            _hoistedLocalDeclarations = prevHoistedDeclarations;
            _terminalVoidReturnToHide = prevTerminalVoidReturnToHide;
            _topLevelRootBlock = prevTopLevelRootBlock;
            _declaredLocals.Clear();
            foreach (var declaredLocal in prevDeclaredLocals)
            {
                _declaredLocals.Add(declaredLocal);
            }
        }

        private static ReturnExpression TryGetReturnExpression(IAstNode node)
        {
            return node switch
            {
                ReturnExpression direct => direct,
                ExpressionStatement { Expression: ReturnExpression wrapped } => wrapped,
                _ => null
            };
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
                (id.Instance is not IdentifierExpression &&
                 id.Instance is not LocalExpression ||
                 ContainsCompositeIdentifierPrefix(id.Instance)))
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

        private static bool ContainsCompositeIdentifierPrefix(Expression instance)
        {
            // FullName 只能安全展开全部由标识符/局部变量组成的链。若中间某层
            // 来自动态索引或调用，例如 table[0].core.name，最外层虽然仍以
            // IdentifierExpression 结尾，也必须递归写出完整接收者。
            while (instance is IdentifierExpression identifier)
            {
                instance = identifier.Instance;
            }

            return instance != null && instance is not LocalExpression;
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

            // 控制流结构化会把一部分赋值移入条件表达式，并清除 IsDeclaration，
            // 以避免生成非法的 `if (var v = ...)`。赋值后来若又作为独立语句落到
            // 一个没有可见声明的词法块，声明分析器会把它重新认定为声明点。
            var treatAsDeclaration = bin.IsDeclaration ||
                                     (_validDeclarations?.Contains(bin) ?? false);

            // 类字段声明属于类体语法，不应套用当前方法的局部变量活跃性结果。
            // 外层方法在写出嵌套类时仍保留自己的声明分析集合，若先查询该集合，
            // `var member;` 会被错误压缩成没有声明意义的 `member;`。
            if (_currentClass != null && treatAsDeclaration &&
                bin.Left is IdentifierExpression &&
                bin.Right is ConstantExpression classVoid && classVoid.DataType == TjsVarType.Void)
            {
                _formatter.WriteIdentifier("var");
                _formatter.WriteSpace();
                Visit(bin.Left);
                var classMemberName = TryGetDeclaredName(bin.Left);
                if (!string.IsNullOrEmpty(classMemberName))
                {
                    _declaredLocals.Add(classMemberName);
                }
                return;
            }

            if (treatAsDeclaration)
            {
                var declaredName = TryGetDeclaredName(bin.Left);
                var shouldEmitVar = true;

                if (_validDeclarations != null)
                {
                    // 使用词法声明预分析结果；未提升的 for 初始化变量仍在此声明。
                    shouldEmitVar = _validDeclarations.Contains(bin) ||
                                    (_inForInitializer && bin.IsDeclaration &&
                                     !_hoistedLocalDeclarations.Contains(declaredName));
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

            bool needBrackets = bin.NeedBrackets();
            if (bin.IsSelfAssignment && bin.Op != BinaryOp.Swap)
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
                   ClassBodies.ContainsKey(classObj.Object);
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
            // 局部/参数寄存器的单独读取没有可观察效果。它通常是短路 Phi 已经
            // 内联到后续表达式后留下的条件标志副本，输出成 `a1;` 只会制造噪声。
            if (expression.Expression is LocalExpression ||
                expression.Expression is ConditionExpression { Condition: LocalExpression })
            {
                return;
            }

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
            if (expression.Expression is IOperation bin &&
                expression.Expression is not BinaryExpression { Op: BinaryOp.Swap })
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
                intIndex.IntValue < 0 || intIndex.IntValue > int.MaxValue)
            {
                return false;
            }

            index = (int)intIndex.IntValue;
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

                if (_inTopLevelBody && !_insideRecoveredTopLevelScope &&
                    ReferenceEquals(block, _topLevelRootBlock) &&
                    TryGetTopLevelLocalScopeEnd(block, i, out var scopeEnd))
                {
                    // 顶层 `var` 只有位于显式语句块内才是局部变量。字节码不会保存
                    // 源码大括号，因此按局部槽从首次定义到最后引用恢复最小闭包。
                    _formatter.WriteToken("{");
                    _formatter.WriteLine();
                    _formatter.Indent();
                    var previousInsideScope = _insideRecoveredTopLevelScope;
                    _insideRecoveredTopLevelScope = true;
                    var scopedBlock = new BlockStatement
                    {
                        Statements = block.Statements.GetRange(i, scopeEnd - i + 1)
                    };
                    var previousValidDeclarations = _validDeclarations;
                    _validDeclarations = previousValidDeclarations == null
                        ? new HashSet<BinaryExpression>(ReferenceEqualityComparer.Instance)
                        : new HashSet<BinaryExpression>(
                            previousValidDeclarations, ReferenceEqualityComparer.Instance);
                    var scopedDeclarations = new LocalDeclarationAnalysis().Analyze(
                        scopedBlock, false);
                    foreach (var localDeclaration in scopedDeclarations.Where(
                                 declaration => declaration.Left is LocalExpression))
                    {
                        // 只合并 VM 局部声明。整个块重新分析得到的 IdentifierExpression
                        // 定义可能是已在外层声明的顶层成员，不能在恢复块中再次输出 var。
                        _validDeclarations.Add(localDeclaration);
                    }
                    VisitBlockStmt(scopedBlock);
                    _validDeclarations = previousValidDeclarations;
                    _insideRecoveredTopLevelScope = previousInsideScope;
                    _formatter.WriteEndBlock();
                    i = scopeEnd;
                    continue;
                }

                if (i > 0 && IsUnconditionalReturnNode(block.Statements[i - 1]) &&
                    IsUnconditionalReturnNode(block.Statements[i]))
                {
                    // 多个循环退出块可能汇合成相邻 return；第一个已终止执行，
                    // 后续 return 永远不可达，重复写出只会制造伪代码噪声。
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

        private static bool TryGetTopLevelLocalScopeEnd(
            BlockStatement block, int startIndex, out int endIndex)
        {
            endIndex = startIndex;
            if (block?.Statements == null || startIndex < 0 ||
                startIndex >= block.Statements.Count ||
                !TryGetDirectLocalDeclaration(
                    block.Statements[startIndex], out var firstLocal))
            {
                return false;
            }

            var locals = new List<LocalExpression> { firstLocal };
            var knownSlots = new HashSet<int> { firstLocal.Slot };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var local in locals.ToArray())
                {
                    var lifetimeEnd = block.Statements.Count;
                    for (var i = startIndex + 1; i < block.Statements.Count; i++)
                    {
                        if (AstNodeDeclaresLocalSlot(block.Statements[i], local.Slot))
                        {
                            lifetimeEnd = i;
                            break;
                        }
                    }

                    for (var i = startIndex; i < lifetimeEnd; i++)
                    {
                        if (AstNodeReferencesExpression(block.Statements[i], local) &&
                            i > endIndex)
                        {
                            endIndex = i;
                            changed = true;
                        }
                    }
                }

                // 已确定的活跃区间内若又声明顶层局部，该局部属于同一恢复块；
                // 将它的最后引用继续并入区间，直到达到固定点。
                for (var i = startIndex; i <= endIndex; i++)
                {
                    if (TryGetDirectLocalDeclaration(
                            block.Statements[i], out var local) &&
                        knownSlots.Add(local.Slot))
                    {
                        locals.Add(local);
                        changed = true;
                    }
                }
            }

            return true;
        }

        private static bool AstNodeDeclaresLocalSlot(IAstNode node, int slot)
        {
            switch (node)
            {
                case null:
                case ConstantExpression _:
                    return false;
                case BinaryExpression binary:
                    return (binary.Op == BinaryOp.Assign && binary.IsDeclaration &&
                            binary.Left is LocalExpression local && local.Slot == slot) ||
                           AstNodeDeclaresLocalSlot(binary.Left, slot) ||
                           AstNodeDeclaresLocalSlot(binary.Right, slot);
                case ExpressionStatement statement:
                    return AstNodeDeclaresLocalSlot(statement.Expression, slot);
                case BlockStatement block:
                    return block.Statements?.Any(statement =>
                        AstNodeDeclaresLocalSlot(statement, slot)) == true;
                case IfStatement statement:
                    return AstNodeDeclaresLocalSlot(statement.Condition, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Then, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Else, slot);
                case ForStatement statement:
                    return AstNodeDeclaresLocalSlot(statement.Initializer, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Condition, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Increment, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Body, slot);
                case WhileStatement statement:
                    return AstNodeDeclaresLocalSlot(statement.Condition, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Body, slot);
                case DoWhileStatement statement:
                    return AstNodeDeclaresLocalSlot(statement.Body, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Condition, slot);
                case SwitchStatement statement:
                    return AstNodeDeclaresLocalSlot(statement.Expression, slot) ||
                           statement.Cases.Any(@case =>
                               @case.Labels.Any(label => AstNodeDeclaresLocalSlot(label, slot)) ||
                               AstNodeDeclaresLocalSlot(@case.Body, slot)) ||
                           AstNodeDeclaresLocalSlot(statement.Default, slot);
                case TryStatement statement:
                    return AstNodeDeclaresLocalSlot(statement.Try, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Catch, slot) ||
                           AstNodeDeclaresLocalSlot(statement.Finally, slot);
                case UnaryExpression expression:
                    return AstNodeDeclaresLocalSlot(expression.Target, slot);
                case ConditionExpression expression:
                    return AstNodeDeclaresLocalSlot(expression.Condition, slot);
                case PropertyAccessExpression expression:
                    return AstNodeDeclaresLocalSlot(expression.Instance, slot) ||
                           AstNodeDeclaresLocalSlot(expression.Property, slot);
                case IdentifierExpression expression:
                    return AstNodeDeclaresLocalSlot(expression.Instance, slot);
                case InvokeExpression expression:
                    return AstNodeDeclaresLocalSlot(expression.Instance, slot) ||
                           AstNodeDeclaresLocalSlot(expression.MethodExpression, slot) ||
                           expression.Parameters?.Any(parameter =>
                               AstNodeDeclaresLocalSlot(parameter, slot)) == true;
                case ReturnExpression expression:
                    return AstNodeDeclaresLocalSlot(expression.Return, slot);
                case ThrowExpression expression:
                    return AstNodeDeclaresLocalSlot(expression.Target, slot);
                case DeleteExpression expression:
                    return AstNodeDeclaresLocalSlot(expression.Instance, slot) ||
                           AstNodeDeclaresLocalSlot(expression.IdentifierExpression, slot);
                default:
                    return false;
            }
        }

        private static bool TryGetDirectLocalDeclaration(
            IAstNode node, out LocalExpression local)
        {
            local = null;
            if (!TryGetDirectLocalDeclarationBinary(node, out var binary))
            {
                return false;
            }

            local = (LocalExpression)binary.Left;
            return true;
        }

        private static bool TryGetDirectLocalDeclarationBinary(
            IAstNode node, out BinaryExpression binary)
        {
            binary = node switch
            {
                BinaryExpression direct => direct,
                ExpressionStatement { Expression: BinaryExpression wrapped } => wrapped,
                ForStatement { Initializer: BinaryExpression initializer } => initializer,
                _ => null
            };
            if (binary == null &&
                !TryFindFirstLocalDeclaration(node, out binary))
            {
                return false;
            }
            if (binary?.Op != BinaryOp.Assign || !binary.IsDeclaration ||
                binary.Left is not LocalExpression target || target.IsParameter)
            {
                binary = null;
                return false;
            }

            return true;
        }

        private static bool TryFindFirstLocalDeclaration(
            IAstNode node, out BinaryExpression declaration)
        {
            declaration = null;
            switch (node)
            {
                case null:
                case ConstantExpression _:
                    return false;
                case BinaryExpression binary:
                    if (binary.Op == BinaryOp.Assign && binary.IsDeclaration &&
                        binary.Left is LocalExpression { IsParameter: false })
                    {
                        declaration = binary;
                        return true;
                    }
                    return TryFindFirstLocalDeclaration(binary.Left, out declaration) ||
                           TryFindFirstLocalDeclaration(binary.Right, out declaration);
                case ExpressionStatement statement:
                    return TryFindFirstLocalDeclaration(statement.Expression, out declaration);
                case BlockStatement block:
                    if (block.Statements != null)
                        foreach (var statement in block.Statements)
                            if (TryFindFirstLocalDeclaration(statement, out declaration))
                                return true;
                    return false;
                case IfStatement statement:
                    return TryFindFirstLocalDeclaration(statement.Condition, out declaration) ||
                           TryFindFirstLocalDeclaration(statement.Then, out declaration) ||
                           TryFindFirstLocalDeclaration(statement.Else, out declaration);
                case ForStatement statement:
                    return TryFindFirstLocalDeclaration(statement.Initializer, out declaration) ||
                           TryFindFirstLocalDeclaration(statement.Body, out declaration) ||
                           TryFindFirstLocalDeclaration(statement.Increment, out declaration);
                case WhileStatement statement:
                    return TryFindFirstLocalDeclaration(statement.Condition, out declaration) ||
                           TryFindFirstLocalDeclaration(statement.Body, out declaration);
                case DoWhileStatement statement:
                    return TryFindFirstLocalDeclaration(statement.Body, out declaration) ||
                           TryFindFirstLocalDeclaration(statement.Condition, out declaration);
                case TryStatement statement:
                    return TryFindFirstLocalDeclaration(statement.Try, out declaration) ||
                           TryFindFirstLocalDeclaration(statement.Finally, out declaration);
                case SwitchStatement statement:
                    foreach (var @case in statement.Cases)
                        if (TryFindFirstLocalDeclaration(@case.Body, out declaration))
                            return true;
                    return TryFindFirstLocalDeclaration(statement.Default, out declaration);
                default:
                    return false;
            }
        }

        private static bool IsUnconditionalReturnNode(IAstNode node)
        {
            return node is ReturnExpression ||
                   node is ExpressionStatement { Expression: ReturnExpression };
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
                    VisitPrefixUnaryTarget(unary.Target);
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
                        VisitPrefixUnaryTarget(unary.Target);
                    }
                    break;
                case UnaryOp.ToNumber:
                    // TJS2 的一元 + 运算符：将值转换为数值类型
                    _formatter.WriteToken("+");
                    VisitPrefixUnaryTarget(unary.Target);
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
                case UnaryOp.ToCharacterCode:
                case UnaryOp.FromCharacterCode:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    VisitPrefixUnaryTarget(unary.Target);
                    break;
                case UnaryOp.IsTrue:
                    Visit(unary.Target);
                    break;
                case UnaryOp.IsFalse:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    VisitPrefixUnaryTarget(unary.Target);
                    break;
                case UnaryOp.TypeOf:
                case UnaryOp.Invalidate:
                case UnaryOp.IsValid:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    _formatter.WriteSpace();
                    VisitPrefixUnaryTarget(unary.Target);
                    break;
                case UnaryOp.PropertyRef:
                    _formatter.WriteToken(unary.Op.ToSymbol());
                    VisitPrefixUnaryTarget(unary.Target);
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

        /// <summary>
        /// 一元前缀运算符必须按当前树形显式保护二元操作数，不能依赖可能因
        /// AST 替换而过期的 Parent 引用，否则 `!(x === void)` 会被写成
        /// 含义不同的 `!x === void`。
        /// </summary>
        private void VisitPrefixUnaryTarget(Expression target)
        {
            var needsGrouping = target is BinaryExpression ||
                                target is ConditionExpression
                                {
                                    Condition: BinaryExpression
                                };
            if (!needsGrouping)
            {
                Visit(target);
                return;
            }

            var originalParent = target.Parent;
            target.Parent = null;
            _formatter.WriteToken("(");
            Visit(target);
            _formatter.WriteToken(")");
            target.Parent = originalParent;
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

        internal override void VisitSwitchStmt(SwitchStatement switchStmt)
        {
            _formatter.WriteKeyword("switch");
            _formatter.WriteSpace();
            _formatter.WriteToken("(");
            Visit(switchStmt.Expression);
            _formatter.WriteToken(")");
            _formatter.WriteStartBlock();
            foreach (var @case in switchStmt.Cases)
            {
                foreach (var label in @case.Labels)
                {
                    _formatter.WriteKeyword("case");
                    _formatter.WriteSpace();
                    Visit(label);
                    _formatter.WriteToken(":");
                    _formatter.WriteLine();
                }

                _formatter.Indent();
                Visit(@case.Body);
                _formatter.Outdent();
            }

            if (switchStmt.Default != null)
            {
                _formatter.WriteKeyword("default");
                _formatter.WriteToken(":");
                _formatter.WriteLine();
                _formatter.Indent();
                Visit(switchStmt.Default);
                _formatter.Outdent();
            }

            _formatter.WriteEndBlock();
            AddNewLineAfterStructCtrlStmt();
        }

        internal override void VisitReturnExpr(ReturnExpression ret)
        {
            if (HideVoidReturn && ReferenceEquals(ret, _terminalVoidReturnToHide))
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
            if (ifStmt.Else != null && IsNoSideEffectBlock(ifStmt.Else))
            {
                // 结构化 CFG 偶尔会在真实 then 后留下只含跳转/纯比较的空 else。
                // 这些节点不会生成代码，也没有可观察副作用，直接移除大括号外壳。
                ifStmt.Else = null;
            }

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

            _formatter.WriteKeyword("if");
            _formatter.WriteSpace();
            _formatter.WriteToken("(");
            Visit(ifStmt.Condition);
            _formatter.WriteToken(")");
            _formatter.WriteStartBlock();
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

            if (ExpressionEffectAnalysis.IsLikelyPure(rawCond))
            {
                return true;
            }

            var effects = new List<Expression>();
            ExpressionEffectAnalysis.CollectObservableEffects(rawCond, effects);
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

                if (statement is ExpressionStatement exprStmt &&
                    exprStmt.Expression != null &&
                    !ExpressionEffectAnalysis.HasObservableEffect(
                        exprStmt.Expression))
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
            return ExpressionEffectAnalysis.IsProvablySideEffectFree(rawCond);
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

            // else-if / 嵌套单臂 if 常直接挂在 Then 上，而不是再包一层
            // BlockStatement。不能把所有非块语句都当成空壳，否则其内部赋值、
            // 调用和 return 会连同外层条件一起被静默删除。
            if (statement is IfStatement directNestedIf)
            {
                return IsStructuralNoOpIfStatement(directNestedIf);
            }

            if (statement == null)
            {
                return true;
            }

            if (statement is not BlockStatement block)
            {
                return false;
            }

            if (block.Statements == null || block.Statements.Count == 0)
            {
                return true;
            }

            foreach (var st in block.Statements)
            {
                if (st is GotoExpression ||
                    st is ConditionExpression rawCondition &&
                    ExpressionEffectAnalysis.IsProvablySideEffectFree(rawCondition))
                {
                    continue;
                }

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

                if (ExpressionEffectAnalysis.HasObservableEffect(
                        exprStmt.Expression))
                {
                    return false;
                }
            }

            return true;
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
                return ia.Name == ib.Name &&
                       ia.IdentifierType == ib.IdentifierType &&
                       IsSameOptionalExpression(ia.Instance, ib.Instance);
            }

            if (a is PropertyAccessExpression pa && b is PropertyAccessExpression pb)
            {
                return IsSameOptionalExpression(pa.Instance, pb.Instance) &&
                       IsSameOptionalExpression(pa.Property, pb.Property);
            }

            if (a is ConstantExpression ca && b is ConstantExpression cb)
            {
                return ca.Variant?.Equals(cb.Variant) ?? cb.Variant == null;
            }

            if (a is UnaryExpression ua && b is UnaryExpression ub)
            {
                return ua.Op == ub.Op && IsSameExpression(ua.Target, ub.Target);
            }

            if (a is BinaryExpression ba && b is BinaryExpression bb)
            {
                return ba.Op == bb.Op &&
                       IsSameExpression(ba.Left, bb.Left) &&
                       IsSameExpression(ba.Right, bb.Right);
            }

            // ToString() 不包含成员实例信息，不能用作兜底比较。例如
            // `+info.value` 与 `+defaults.value` 的字符串形状相同，但语义不同。
            return false;
        }

        private static bool IsSameOptionalExpression(Expression a, Expression b)
        {
            return a == null ? b == null : b != null && IsSameExpression(a, b);
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
                WhileStatement whileStmt =>
                    AstNodeReferencesExpression(whileStmt.Condition, target) ||
                    AstNodeReferencesExpression(whileStmt.Body, target),
                DoWhileStatement doWhileStmt =>
                    AstNodeReferencesExpression(doWhileStmt.Condition, target) ||
                    AstNodeReferencesExpression(doWhileStmt.Body, target),
                ForStatement forStmt =>
                    AstNodeReferencesExpression(forStmt.Initializer, target) ||
                    AstNodeReferencesExpression(forStmt.Condition, target) ||
                    AstNodeReferencesExpression(forStmt.Increment, target) ||
                    AstNodeReferencesExpression(forStmt.Body, target),
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
                ConditionExpression condition =>
                    AstNodeReferencesExpression(condition.Condition, target),
                PhiExpression phi =>
                    AstNodeReferencesExpression(phi.Condition, target) ||
                    AstNodeReferencesExpression(phi.ThenBranch, target) ||
                    AstNodeReferencesExpression(phi.ElseBranch, target) ||
                    (phi.PossibleExpressions?.Any(expression =>
                        AstNodeReferencesExpression(expression, target)) ?? false),
                DeleteExpression delete =>
                    AstNodeReferencesExpression(delete.Instance, target) ||
                    AstNodeReferencesExpression(delete.IdentifierExpression, target),
                InvokeExpression invoke =>
                    AstNodeReferencesExpression(invoke.Instance, target) ||
                    AstNodeReferencesExpression(invoke.MethodExpression, target) ||
                    (invoke.Parameters?.Any(p => AstNodeReferencesExpression(p, target)) ?? false),
                ReturnExpression ret =>
                    AstNodeReferencesExpression(ret.Return, target),
                ThrowExpression thrown =>
                    AstNodeReferencesExpression(thrown.Target, target),
                TryStatement tryInner =>
                    AstNodeReferencesExpression(tryInner.Try, target) ||
                    AstNodeReferencesExpression(tryInner.Catch, target) ||
                    AstNodeReferencesExpression(tryInner.Finally, target),
                _ => false
            };
        }

    }
}
