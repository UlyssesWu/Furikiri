using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 为仍在迁移中的控制流识别器提供原子提交边界。
    /// </summary>
    /// <remarks>
    /// 候选识别期间可以递归物化子区域，但只有整个候选成功后这些修改才可见。
    /// 失败时恢复基本块可见性、语句列表和原有条件节点，避免后续识别器消费半成品。
    /// 新的区域分析器仍应优先产出只读计划；本类型用于约束尚未完成迁移的旧路径。
    /// </remarks>
    internal sealed class ControlFlowMutationScope : IDisposable
    {
        private readonly List<BlockState> _blocks;
        private readonly List<BlockStatementState> _blockStatements =
            new List<BlockStatementState>();
        private readonly List<ConditionState> _conditions =
            new List<ConditionState>();
        private readonly List<Action> _nodeRestorers = new List<Action>();
        private readonly Dictionary<IAstNode, IAstNode> _parents =
            new Dictionary<IAstNode, IAstNode>(ReferenceEqualityComparer.Instance);
        private bool _committed;

        public ControlFlowMutationScope(IEnumerable<Block> blocks)
        {
            _blocks = blocks?
                .Where(block => block != null)
                .Distinct()
                .Select(block => new BlockState(block))
                .ToList() ?? new List<BlockState>();

            var visited = new HashSet<IAstNode>(ReferenceEqualityComparer.Instance);
            foreach (var block in _blocks)
            {
                foreach (var statement in block.Statements)
                {
                    CaptureAst(statement, visited);
                }
            }
        }

        public void Commit()
        {
            _committed = true;
        }

        /// <summary>
        /// 检查候选物化是否修改了声明所有权之外的基本块。
        /// </summary>
        /// <remarks>
        /// 区域计划只允许隐藏或重排自己拥有的块。旧递归结构化器若越过公共
        /// 继续点，本方法会在提交前发现可见性或语句列表变化，由外层作用域
        /// 统一回滚。AST 节点内部的改写必须先通过所属块的列表替换进入结果；
        /// 因而这里以块可见性和语句引用序列作为所有权提交边界。
        /// </remarks>
        public bool HasChangesOutside(ISet<Block> ownedBlocks)
        {
            return _blocks.Any(state =>
                (ownedBlocks == null || !ownedBlocks.Contains(state.Block)) &&
                !state.MatchesCurrent());
        }

        public void Dispose()
        {
            if (_committed)
            {
                return;
            }

            // 先恢复容器，再恢复节点字段和导航父节点。这样即使候选把原列表整体
            // 替换掉，外部仍会重新看到进入识别器前的同一个列表对象。
            foreach (var state in _blocks)
            {
                state.Restore();
            }
            foreach (var state in _blockStatements)
            {
                state.Restore();
            }
            foreach (var state in _conditions)
            {
                state.Restore();
            }
            foreach (var restore in _nodeRestorers)
            {
                restore();
            }
            foreach (var parent in _parents)
            {
                parent.Key.Parent = parent.Value;
            }
        }

        private void CaptureAst(IAstNode node, ISet<IAstNode> visited)
        {
            if (node == null || !visited.Add(node))
            {
                return;
            }

            _parents[node] = node.Parent;
            if (node is BlockStatement block)
            {
                _blockStatements.Add(new BlockStatementState(block));
            }
            if (node is ConditionExpression condition)
            {
                _conditions.Add(new ConditionState(condition));
            }
            CaptureMutableFields(node);

            foreach (var child in GetChildren(node))
            {
                CaptureAst(child, visited);
            }
        }

        private static IEnumerable<IAstNode> GetChildren(IAstNode node)
        {
            // 部分旧 AST 类型的 Children 尚未覆盖自己的所有字段，先显式补齐这些
            // 类型；其余节点继续使用统一 Children 接口。
            switch (node)
            {
                case ConditionExpression condition:
                    if (condition.Condition != null) yield return condition.Condition;
                    yield break;
                case IfStatement statement:
                    if (statement.Condition != null) yield return statement.Condition;
                    if (statement.Then != null) yield return statement.Then;
                    if (statement.Else != null) yield return statement.Else;
                    yield break;
                case InvokeExpression invoke:
                    if (invoke.Instance != null) yield return invoke.Instance;
                    if (invoke.MethodExpression != null) yield return invoke.MethodExpression;
                    if (invoke.Parameters != null)
                    {
                        foreach (var parameter in invoke.Parameters)
                        {
                            if (parameter != null) yield return parameter;
                        }
                    }
                    yield break;
                case ForStatement statement:
                    if (statement.Initializer != null) yield return statement.Initializer;
                    if (statement.Condition != null) yield return statement.Condition;
                    if (statement.Increment != null) yield return statement.Increment;
                    if (statement.Body != null) yield return statement.Body;
                    yield break;
                case WhileStatement statement:
                    if (statement.Condition != null) yield return statement.Condition;
                    if (statement.Body != null) yield return statement.Body;
                    yield break;
                case DoWhileStatement statement:
                    if (statement.Condition != null) yield return statement.Condition;
                    if (statement.Body != null) yield return statement.Body;
                    yield break;
                case TryStatement statement:
                    if (statement.Try != null) yield return statement.Try;
                    if (statement.Catch != null) yield return statement.Catch;
                    if (statement.Finally != null) yield return statement.Finally;
                    yield break;
                case ExpressionStatement statement:
                    if (statement.Expression != null) yield return statement.Expression;
                    yield break;
            }

            if (node.Children == null)
            {
                yield break;
            }
            foreach (var child in node.Children)
            {
                if (child != null)
                {
                    yield return child;
                }
            }
        }

        private void CaptureMutableFields(IAstNode node)
        {
            // 控制流阶段主要会改写表达式引用、循环条件和已物化语句的子树。
            // 在这里集中登记恢复动作，避免每个识别器各自维护一套回滚代码。
            switch (node)
            {
                case BinaryExpression binary:
                {
                    var left = binary.Left;
                    var right = binary.Right;
                    var op = binary.Op;
                    var self = binary.IsSelfAssignment;
                    var declaration = binary.IsDeclaration;
                    var resultType = binary.ResultType;
                    _nodeRestorers.Add(() =>
                    {
                        binary.Left = left;
                        binary.Right = right;
                        binary.Op = op;
                        binary.IsSelfAssignment = self;
                        binary.IsDeclaration = declaration;
                        binary.ResultType = resultType;
                    });
                    break;
                }
                case UnaryExpression unary:
                {
                    var target = unary.Target;
                    var op = unary.Op;
                    var self = unary.IsSelfAssignment;
                    var prefix = unary.IsPrefix;
                    var resultType = unary.ResultType;
                    _nodeRestorers.Add(() =>
                    {
                        unary.Target = target;
                        unary.Op = op;
                        unary.IsSelfAssignment = self;
                        unary.IsPrefix = prefix;
                        unary.ResultType = resultType;
                    });
                    break;
                }
                case PhiExpression phi:
                {
                    var slot = phi.Slot;
                    var condition = phi.Condition;
                    var thenBranch = phi.ThenBranch;
                    var elseBranch = phi.ElseBranch;
                    var possibleList = phi.PossibleExpressions;
                    var possible = possibleList?.ToArray() ?? Array.Empty<Expression>();
                    _nodeRestorers.Add(() =>
                    {
                        phi.Slot = slot;
                        phi.Condition = condition;
                        phi.ThenBranch = thenBranch;
                        phi.ElseBranch = elseBranch;
                        if (possibleList != null)
                        {
                            possibleList.Clear();
                            possibleList.AddRange(possible);
                        }
                        phi.PossibleExpressions = possibleList;
                    });
                    break;
                }
                case IdentifierExpression identifier:
                {
                    var name = identifier.Name;
                    var type = identifier.IdentifierType;
                    var hide = identifier.HideInstance;
                    var instance = identifier.Instance;
                    _nodeRestorers.Add(() =>
                    {
                        identifier.Name = name;
                        identifier.IdentifierType = type;
                        identifier.HideInstance = hide;
                        identifier.Instance = instance;
                    });
                    break;
                }
                case PropertyAccessExpression property:
                {
                    var instance = property.Instance;
                    var member = property.Property;
                    _nodeRestorers.Add(() =>
                    {
                        property.Instance = instance;
                        property.Property = member;
                    });
                    break;
                }
                case InvokeExpression invoke:
                {
                    var invokeType = invoke.InvokeType;
                    var methodName = invoke.MethodName;
                    var methodObject = invoke.MethodObject;
                    var methodExpression = invoke.MethodExpression;
                    var instance = invoke.Instance;
                    var parametersList = invoke.Parameters;
                    var parameters = parametersList?.ToArray() ?? Array.Empty<Expression>();
                    var omitted = invoke.HasOmittedArguments;
                    var spreadSet = invoke.SpreadParameterIndices;
                    var spread = spreadSet?.ToArray() ?? Array.Empty<int>();
                    _nodeRestorers.Add(() =>
                    {
                        invoke.InvokeType = invokeType;
                        invoke.MethodName = methodName;
                        invoke.MethodObject = methodObject;
                        invoke.MethodExpression = methodExpression;
                        invoke.Instance = instance;
                        if (parametersList != null)
                        {
                            parametersList.Clear();
                            parametersList.AddRange(parameters);
                        }
                        invoke.Parameters = parametersList;
                        invoke.HasOmittedArguments = omitted;
                        if (spreadSet != null)
                        {
                            spreadSet.Clear();
                            spreadSet.UnionWith(spread);
                        }
                        invoke.SpreadParameterIndices = spreadSet;
                    });
                    break;
                }
                case IfStatement statement:
                {
                    var condition = statement.Condition;
                    var then = statement.Then;
                    var @else = statement.Else;
                    var elseIf = statement.IsElseIf;
                    var equalityDispatch = statement.IsEqualityDispatch;
                    var compilerSwitchEvidence =
                        statement.HasCompilerSwitchEvidence;
                    var compilerDefaultEvidence =
                        statement.HasCompilerDefaultEvidence;
                    var defaultFallthrough =
                        statement.HasEqualityDispatchDefaultFallthrough;
                    var fallsThroughToDefault =
                        statement.FallsThroughToEqualityDispatchDefault;
                    var endsAtLoopLatch =
                        statement.EqualityDispatchEndsAtLoopLatch;
                    _nodeRestorers.Add(() =>
                    {
                        statement.Condition = condition;
                        statement.Then = then;
                        statement.Else = @else;
                        statement.IsElseIf = elseIf;
                        statement.IsEqualityDispatch = equalityDispatch;
                        statement.HasCompilerSwitchEvidence =
                            compilerSwitchEvidence;
                        statement.HasCompilerDefaultEvidence =
                            compilerDefaultEvidence;
                        statement.HasEqualityDispatchDefaultFallthrough =
                            defaultFallthrough;
                        statement.FallsThroughToEqualityDispatchDefault =
                            fallsThroughToDefault;
                        statement.EqualityDispatchEndsAtLoopLatch =
                            endsAtLoopLatch;
                    });
                    break;
                }
                case ExpressionStatement statement:
                {
                    var expression = statement.Expression;
                    _nodeRestorers.Add(() => statement.Expression = expression);
                    break;
                }
                case ForStatement statement:
                {
                    var initializer = statement.Initializer;
                    var condition = statement.Condition;
                    var increment = statement.Increment;
                    var body = statement.Body;
                    _nodeRestorers.Add(() =>
                    {
                        statement.Initializer = initializer;
                        statement.Condition = condition;
                        statement.Increment = increment;
                        statement.Body = body;
                    });
                    break;
                }
                case WhileStatement statement:
                {
                    var condition = statement.Condition;
                    var body = statement.Body;
                    _nodeRestorers.Add(() =>
                    {
                        statement.Condition = condition;
                        statement.Body = body;
                    });
                    break;
                }
                case DoWhileStatement statement:
                {
                    var condition = statement.Condition;
                    var body = statement.Body;
                    _nodeRestorers.Add(() =>
                    {
                        statement.Condition = condition;
                        statement.Body = body;
                    });
                    break;
                }
                case ReturnExpression expression:
                {
                    var value = expression.Return;
                    _nodeRestorers.Add(() => expression.Return = value);
                    break;
                }
                case ThrowExpression expression:
                {
                    var target = expression.Target;
                    _nodeRestorers.Add(() => expression.Target = target);
                    break;
                }
                case DeleteExpression expression:
                {
                    var instance = expression.Instance;
                    var name = expression.IdentifierName;
                    var identifier = expression.IdentifierExpression;
                    _nodeRestorers.Add(() =>
                    {
                        expression.Instance = instance;
                        expression.IdentifierName = name;
                        expression.IdentifierExpression = identifier;
                    });
                    break;
                }
                case CatchExpression expression:
                {
                    var exception = expression.Exception;
                    _nodeRestorers.Add(() => expression.Exception = exception);
                    break;
                }
                case TryStatement statement:
                {
                    var @try = statement.Try;
                    var @catch = statement.Catch;
                    var @finally = statement.Finally;
                    _nodeRestorers.Add(() =>
                    {
                        statement.Try = @try;
                        statement.Catch = @catch;
                        statement.Finally = @finally;
                    });
                    break;
                }
                case SwitchStatement statement:
                {
                    var expression = statement.Expression;
                    var @default = statement.Default;
                    var cases = statement.Cases.ToArray();
                    _nodeRestorers.Add(() =>
                    {
                        statement.Expression = expression;
                        statement.Default = @default;
                        statement.Cases.Clear();
                        statement.Cases.AddRange(cases);
                    });
                    foreach (var @case in cases)
                    {
                        var labels = @case.Labels.ToArray();
                        var body = @case.Body;
                        _nodeRestorers.Add(() =>
                        {
                            @case.Labels.Clear();
                            @case.Labels.AddRange(labels);
                            @case.Body = body;
                        });
                    }
                    break;
                }
            }
        }

        private sealed class BlockState
        {
            private readonly Block _block;
            private readonly bool _hidden;
            private readonly List<IAstNode> _originalList;
            private readonly IAstNode[] _statements;

            public IEnumerable<IAstNode> Statements => _statements;
            public Block Block => _block;

            public BlockState(Block block)
            {
                _block = block;
                _hidden = block.Hidden;
                _originalList = block.Statements;
                _statements = block.Statements?.ToArray() ?? Array.Empty<IAstNode>();
            }

            public void Restore()
            {
                _block.Hidden = _hidden;
                if (_originalList == null)
                {
                    _block.Statements = null;
                    return;
                }

                _originalList.Clear();
                _originalList.AddRange(_statements);
                _block.Statements = _originalList;
            }

            public bool MatchesCurrent()
            {
                return _block.Hidden == _hidden &&
                       ReferenceEquals(_block.Statements, _originalList) &&
                       (_block.Statements?.SequenceEqual(_statements) ??
                        _statements.Length == 0);
            }
        }

        private sealed class BlockStatementState
        {
            private readonly BlockStatement _block;
            private readonly List<IAstNode> _originalList;
            private readonly IAstNode[] _statements;
            private readonly List<Block> _blocks;
            private readonly bool _resolved;

            public BlockStatementState(BlockStatement block)
            {
                _block = block;
                _originalList = block.Statements;
                _statements = block.Statements?.ToArray() ?? Array.Empty<IAstNode>();
                _blocks = block.Blocks == null ? null : new List<Block>(block.Blocks);
                _resolved = block.Resolved;
            }

            public void Restore()
            {
                if (_originalList == null)
                {
                    _block.Statements = null;
                }
                else
                {
                    _originalList.Clear();
                    _originalList.AddRange(_statements);
                    _block.Statements = _originalList;
                }
                _block.Blocks = _blocks == null ? null : new List<Block>(_blocks);
                _block.Resolved = _resolved;
            }
        }

        private sealed class ConditionState
        {
            private readonly ConditionExpression _condition;
            private readonly Expression _expression;
            private readonly bool _jumpIf;
            private readonly int _jumpTo;
            private readonly int _elseTo;

            public ConditionState(ConditionExpression condition)
            {
                _condition = condition;
                _expression = condition.Condition;
                _jumpIf = condition.JumpIf;
                _jumpTo = condition.JumpTo;
                _elseTo = condition.ElseTo;
            }

            public void Restore()
            {
                _condition.Condition = _expression;
                _condition.JumpIf = _jumpIf;
                _condition.JumpTo = _jumpTo;
                _condition.ElseTo = _elseTo;
            }
        }
    }
}
