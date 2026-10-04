using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Language
{
    /// <summary>
    /// 从结构化 AST 恢复局部变量的词法声明位置。
    /// VM 的 CP 指令不区分声明与普通赋值，而结构化过程还可能移动表达式或合并块，
    /// 因此声明不能只依赖单条指令的 IsDeclaration 标记。
    /// </summary>
    internal sealed class LocalDeclarationAnalysis
    {
        // 需要在原位置输出 var 的赋值节点，使用引用相等避免结构相同的赋值互相混淆。
        private readonly HashSet<BinaryExpression> _validDeclarations =
            new HashSet<BinaryExpression>(ReferenceEqualityComparer.Instance);

        // TJS2 的 var 以语句块为词法作用域。VM 会在块结束后复用局部槽，
        // 因此不能用一个函数级集合把同槽位的后续变量当成普通赋值。
        private readonly Stack<HashSet<string>> _scopes =
            new Stack<HashSet<string>>();

        private readonly Dictionary<string, int> _validDefinitionCounts =
            new Dictionary<string, int>();

        private readonly HashSet<string> _hoistedDeclarations =
            new HashSet<string>();

        private readonly HashSet<string> _unresolvedLocalReferences =
            new HashSet<string>();

        internal IReadOnlyCollection<string> HoistedDeclarations =>
            _hoistedDeclarations.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        private bool _vmLocalsOnly;

        internal HashSet<BinaryExpression> Analyze(
            BlockStatement body, bool allowMethodLocalHoisting = true, bool vmLocalsOnly = false)
        {
            _vmLocalsOnly = vmLocalsOnly;
            Walk(body);
            if (!allowMethodLocalHoisting)
            {
                return _validDeclarations;
            }

            foreach (var definition in _validDefinitionCounts)
            {
                if (definition.Value > 1)
                {
                    _hoistedDeclarations.Add(definition.Key);
                }
            }

            _hoistedDeclarations.UnionWith(_unresolvedLocalReferences);
            _validDeclarations.RemoveWhere(binary =>
            {
                var name = TryGetDeclaredName(binary.Left);
                return !string.IsNullOrEmpty(name) &&
                       _hoistedDeclarations.Contains(name);
            });
            return _validDeclarations;
        }

        private void ProcessDefinition(BinaryExpression binary, string name)
        {
            if (_scopes.Count == 0)
            {
                _scopes.Push(new HashSet<string>());
            }

            if (_scopes.Any(scope => scope.Contains(name)))
            {
                return;
            }

            // 当前及所有祖先块都没有该名字：这是本块的新局部变量。
            _validDeclarations.Add(binary);
            _scopes.Peek().Add(name);
            _validDefinitionCounts.TryGetValue(name, out var count);
            _validDefinitionCounts[name] = count + 1;
        }

        private void WalkBlock(BlockStatement block, bool createScope = true)
        {
            if (block?.Statements == null)
            {
                return;
            }

            if (createScope)
            {
                _scopes.Push(new HashSet<string>());
            }

            foreach (var statement in block.Statements)
            {
                // 只有词法块直接包含的语句才天然处于可声明位置。条件节点也可能
                // 为统一 AST 形状套一层 ExpressionStatement，不能据此推断 var。
                WalkDeclarationPosition(statement);
            }

            if (createScope)
            {
                _scopes.Pop();
            }
        }

        private void Walk(IAstNode node)
        {
            switch (node)
            {
                case null:
                    return;
                case BlockStatement block:
                    WalkBlock(block);
                    break;
                case ExpressionStatement statement:
                    Walk(statement.Expression);
                    break;
                case BinaryExpression binary:
                    WalkBinary(binary);
                    break;
                case LocalExpression local:
                    if (!local.IsParameter &&
                        !_scopes.Any(scope => scope.Contains(local.ToString())))
                    {
                        // 结构化 AST 中读取越过了原声明块，只能把该 VM 槽提升到方法入口。
                        _unresolvedLocalReferences.Add(local.ToString());
                    }
                    break;
                case IdentifierExpression identifier:
                    Walk(identifier.Instance);
                    break;
                case IfStatement statement:
                    Walk(statement.Condition);
                    Walk(statement.Then);
                    Walk(statement.Else);
                    break;
                case ForStatement statement:
                    // 初始化节声明的变量对条件、正文和增量可见，但不自动延伸到循环之后。
                    _scopes.Push(new HashSet<string>());
                    WalkDeclarationPosition(statement.Initializer);
                    Walk(statement.Condition);
                    Walk(statement.Body);
                    Walk(statement.Increment);
                    _scopes.Pop();
                    break;
                case WhileStatement statement:
                    Walk(statement.Condition);
                    Walk(statement.Body);
                    break;
                case DoWhileStatement statement:
                    Walk(statement.Body);
                    Walk(statement.Condition);
                    break;
                case TryStatement statement:
                    Walk(statement.Try);
                    WalkCatch(statement);
                    break;
                case SwitchStatement statement:
                    WalkSwitch(statement);
                    break;
                case ReturnExpression expression:
                    Walk(expression.Return);
                    break;
                case UnaryExpression expression:
                    // INC/DEC 等自赋值读取目标，不产生新声明。
                    Walk(expression.Target);
                    break;
                case InvokeExpression expression:
                    Walk(expression.Instance);
                    Walk(expression.MethodExpression);
                    if (expression.Parameters != null)
                    {
                        foreach (var parameter in expression.Parameters)
                        {
                            Walk(parameter);
                        }
                    }
                    break;
                case ConditionExpression expression:
                    Walk(expression.Condition);
                    break;
                case PhiExpression expression:
                    Walk(expression.Condition?.Condition);
                    Walk(expression.ThenBranch);
                    Walk(expression.ElseBranch);
                    if (expression.PossibleExpressions != null)
                    {
                        foreach (var possible in expression.PossibleExpressions)
                        {
                            Walk(possible);
                        }
                    }
                    break;
                case PropertyAccessExpression expression:
                    Walk(expression.Instance);
                    Walk(expression.Property);
                    break;
                case ThrowExpression expression:
                    Walk(expression.Target);
                    break;
                case DeleteExpression expression:
                    Walk(expression.Instance);
                    Walk(expression.IdentifierExpression);
                    break;
                case ConstantExpression _:
                    // Lambda/闭包拥有独立作用域，不向下分析。
                    break;
                case CatchExpression _:
                    // catch 变量属于写入目标，不作为读取处理。
                    break;
                // BreakStatement、ContinueStatement 无子节点。
            }
        }

        private void WalkCatch(TryStatement statement)
        {
            if (statement.Catch?.Expression is not CatchExpression catchClause ||
                !TryGetCatchVariableCopy(
                    statement.Finally, catchClause.Exception,
                    out var catchParameter, out var catchCopy))
            {
                Walk(statement.Finally);
                return;
            }

            // catch 参数与正文共用 catch 的块作用域；隐式异常拷贝由 catch 语法承载，
            // 既不输出，也不再声明一次。
            _scopes.Push(new HashSet<string>());
            var catchName = TryGetDeclaredName(catchParameter);
            if (!string.IsNullOrEmpty(catchName))
            {
                _scopes.Peek().Add(catchName);
            }

            if (statement.Finally?.Statements != null)
            {
                foreach (var child in statement.Finally.Statements)
                {
                    if (!ReferenceEquals(child, catchCopy))
                    {
                        WalkDeclarationPosition(child);
                    }
                }
            }
            _scopes.Pop();
        }

        private void WalkSwitch(SwitchStatement statement)
        {
            Walk(statement.Expression);
            // Case.Body 保留控制流区域的块边界。输出可以不显式加大括号，但声明分析
            // 必须按独立区域检查；多 case 重用同一槽时再提升到方法入口。
            _scopes.Push(new HashSet<string>());
            foreach (var @case in statement.Cases)
            {
                foreach (var label in @case.Labels)
                {
                    Walk(label);
                }
                WalkBlock(@case.Body);
            }
            WalkBlock(statement.Default);
            _scopes.Pop();
        }

        private void WalkDeclarationPosition(IAstNode node)
        {
            switch (node)
            {
                case ExpressionStatement statement:
                    WalkDeclarationPosition(statement.Expression);
                    break;
                case BinaryExpression binary:
                    WalkBinary(binary, true);
                    break;
                default:
                    Walk(node);
                    break;
            }
        }

        private void WalkBinary(BinaryExpression binary, bool canDeclareHere = false)
        {
            // 只有独立表达式语句和 for 初始化器能补回被清除的声明标记。条件、调用参数、
            // return 等内部即使首次出现局部赋值，也不能在表达式中插入 var。
            var inferredLocalDeclaration = canDeclareHere &&
                                           binary.Op == BinaryOp.Assign &&
                                           binary.Left is LocalExpression { IsParameter: false };
            // 参数槽在函数签名处已经声明。CP 指令本身不携带源码的 var 信息，早期
            // 表达式恢复可能仍把“首次写参数”标为声明；若在这里再次认领，会输出
            // `var a0 = ...`，默认参数被提升到签名后还可能进一步生成入口 `var a0;`。
            // 参数写入始终是普通赋值，不能参与局部槽的作用域和提升计数。
            var writesParameter = binary.Left is LocalExpression { IsParameter: true };
            if (!writesParameter &&
                (binary.IsDeclaration || inferredLocalDeclaration) &&
                !binary.IsSelfAssignment)
            {
                Walk(binary.Right);
                var name = TryGetDeclaredName(binary.Left);
                if (!string.IsNullOrEmpty(name))
                {
                    ProcessDefinition(binary, name);
                }
                else
                {
                    Walk(binary.Left);
                }
                return;
            }

            // 自赋值和普通运算的两侧均属于读取上下文。
            Walk(binary.Left);
            Walk(binary.Right);
        }

        private string TryGetDeclaredName(Expression expression)
        {
            if (_vmLocalsOnly && expression is not LocalExpression) return null;
            return expression switch
            {
                LocalExpression local => local.ToString(),
                IdentifierExpression identifier when identifier.Instance == null => identifier.Name,
                IdentifierExpression identifier when !identifier.FullName.Contains(".") => identifier.Name,
                _ => null
            };
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
                if (node is ExpressionStatement statement &&
                    statement.Expression is BinaryExpression binary &&
                    binary.Op == BinaryOp.Assign &&
                    AreSameVariable(binary.Right, implicitException))
                {
                    parameter = binary.Left;
                    copyNode = node;
                    return true;
                }
            }
            return false;
        }

        private static bool AreSameVariable(Expression left, Expression right)
        {
            return ReferenceEquals(left, right) ||
                   left is LocalExpression local && right is LocalExpression targetLocal &&
                   local.Slot == targetLocal.Slot ||
                   left is IdentifierExpression identifier && right is IdentifierExpression targetIdentifier &&
                   identifier.FullName == targetIdentifier.FullName;
        }
    }
}
