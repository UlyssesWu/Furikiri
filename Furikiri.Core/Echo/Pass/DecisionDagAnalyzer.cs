using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 纯条件 DAG 到单个叶子载荷的只读结构计划。
    /// </summary>
    /// <remarks>
    /// 计划只记录区域归属和已合成的路径谓词，不修改语句或基本块可见性。
    /// 调用方确认采用计划后，才统一隐藏内部决策块并生成 IfLogic。
    /// </remarks>
    internal abstract class DecisionDagRegionPlan
    {
        public Block Root { get; set; }
        public Expression Predicate { get; set; }
        public IReadOnlyCollection<Block> Decisions { get; set; }
        public IReadOnlyCollection<Block> PassthroughBlocks { get; set; }

        /// <summary>
        /// 当前计划独占的条件与跳板块。叶子正文和公共继续块由具体计划解释，
        /// 不在分析成功前改变任何块的可见性。
        /// </summary>
        public IEnumerable<Block> OwnedControlBlocks =>
            Decisions.Concat(PassthroughBlocks).Distinct();
    }

    internal sealed class DecisionDagPlan : DecisionDagRegionPlan
    {
        public Block Continuation { get; set; }
        public Block Body { get; set; }
    }

    /// <summary>
    /// 纯条件 DAG 到提前返回守卫的只读结构计划。
    /// </summary>
    internal sealed class DecisionDagReturnPlan : DecisionDagRegionPlan
    {
        public Block ReturnTarget { get; set; }
        public Block BodyTarget { get; set; }
    }

    /// <summary>
    /// 纯条件 DAG 在两个独立叶子载荷之间作选择的只读结构计划。
    /// </summary>
    internal sealed class DecisionDagBranchPlan : DecisionDagRegionPlan
    {
        public Block TrueTarget { get; set; }
        public Block FalseTarget { get; set; }
        public Block Continuation { get; set; }
    }

    /// <summary>
    /// 识别最终只选择“执行一个叶子载荷”或“直接继续”的共享决策 DAG。
    /// </summary>
    internal static class DecisionDagAnalyzer
    {
        /// <summary>
        /// 决策 DAG 的不可变分析快照。它只描述控制块、跳板、终点和每个节点
        /// 的有效条件；具体是 return、单载荷还是双载荷由上层验证。
        /// </summary>
        private sealed class DecisionDagTopology
        {
            public Block Root { get; set; }
            public IReadOnlyCollection<Block> Decisions { get; set; }
            public IReadOnlyCollection<Block> PassthroughBlocks { get; set; }
            public IReadOnlyCollection<Block> Terminals { get; set; }
            public IReadOnlyDictionary<Block, Expression> Conditions { get; set; }
        }

        /// <summary>
        /// 统一遍历条件图。分析过程中只读取 CFG 快照；即使候选最终被拒绝，
        /// 也不会留下 Hidden 标记或删除跳转。
        /// </summary>
        private static bool TryAnalyzeTopology(
            DecompileContext context, ControlFlowGraphAnalysis graph, Block root,
            System.Func<Block, Expression> getDecisionCondition,
            Block preservedTerminal, out DecisionDagTopology topology)
        {
            topology = null;
            if (context?.BlockTable == null || graph == null || root == null ||
                getDecisionCondition?.Invoke(root) == null)
            {
                return false;
            }

            var decisions = new HashSet<Block> { root };
            var passthrough = new HashSet<Block>();
            var terminals = new HashSet<Block>();
            var conditions = new Dictionary<Block, Expression>();
            var pending = new Stack<Block>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var condition = current.Statements.GetCondition();
                var effectiveCondition = getDecisionCondition(current);
                if (condition == null || effectiveCondition == null ||
                    !TryGetBranch(context, condition.TrueBranch, out var trueStart) ||
                    !TryGetBranch(context, condition.FalseBranch, out var falseStart))
                {
                    return false;
                }

                conditions[current] = effectiveCondition;
                foreach (var start in new[] { trueStart, falseStart })
                {
                    // 已知公共出口必须作为边界保留，不能穿过已隐藏的条件壳进入
                    // 下一条源码语句。
                    var target = start == preservedTerminal
                        ? start
                        : NormalizeTarget(graph, start, passthrough);
                    var nextCondition = target == preservedTerminal
                        ? null
                        : getDecisionCondition(target);
                    if (target != root && nextCondition != null)
                    {
                        if (decisions.Add(target))
                        {
                            pending.Push(target);
                        }
                    }
                    else if (!decisions.Contains(target))
                    {
                        terminals.Add(target);
                    }
                }
            }

            topology = new DecisionDagTopology
            {
                Root = root,
                Decisions = decisions.ToArray(),
                PassthroughBlocks = passthrough.ToArray(),
                Terminals = terminals.ToArray(),
                Conditions = conditions
            };
            return true;
        }

        /// <summary>
        /// 在已经验证的两个语义终点之间合成根路径谓词。
        /// </summary>
        private static bool TryBuildPredicate(
            DecompileContext context, ControlFlowGraphAnalysis graph,
            DecisionDagTopology topology, Block trueTarget, Block falseTarget,
            out Expression expression)
        {
            expression = null;
            if (topology == null || trueTarget == null || falseTarget == null ||
                trueTarget == falseTarget)
            {
                return false;
            }

            var decisions = topology.Decisions.ToHashSet();
            var passthrough = topology.PassthroughBlocks.ToHashSet();
            var memo = new Dictionary<Block, PathPredicate>();
            var visiting = new HashSet<Block>();
            PathPredicate Build(Block current)
            {
                if (current == trueTarget)
                {
                    return PathPredicate.True();
                }
                if (current == falseTarget)
                {
                    return PathPredicate.False();
                }

                current = NormalizeTarget(graph, current, passthrough);
                if (current == trueTarget)
                {
                    return PathPredicate.True();
                }
                if (current == falseTarget)
                {
                    return PathPredicate.False();
                }
                if (!decisions.Contains(current))
                {
                    return null;
                }
                if (memo.TryGetValue(current, out var cached))
                {
                    return cached;
                }
                if (!visiting.Add(current))
                {
                    return null;
                }

                var condition = current.Statements.GetCondition();
                if (condition == null ||
                    !topology.Conditions.TryGetValue(current, out var effectiveCondition) ||
                    !TryGetBranch(context, condition.TrueBranch, out var whenTrueBlock) ||
                    !TryGetBranch(context, condition.FalseBranch, out var whenFalseBlock))
                {
                    visiting.Remove(current);
                    return null;
                }

                var whenTrue = Build(whenTrueBlock);
                var whenFalse = Build(whenFalseBlock);
                visiting.Remove(current);
                if (whenTrue == null || whenFalse == null)
                {
                    return null;
                }

                var result = PathPredicate.Combine(
                    effectiveCondition, whenTrue, whenFalse);
                memo[current] = result;
                return result;
            }

            var predicate = Build(topology.Root);
            if (predicate?.Expression == null || predicate.Constant != null)
            {
                return false;
            }

            expression = predicate.Expression;
            return true;
        }

        /// <summary>
        /// 识别多个纯条件最终选择两个叶子载荷、随后在同一块汇合的区域。
        /// 共享的 false 载荷不是公共继续点，必须作为完整 else 保留。
        /// </summary>
        public static bool TryAnalyzeTwoPayloadBranches(
            DecompileContext context, ControlFlowGraphAnalysis graph,
            Block root, out DecisionDagBranchPlan plan)
        {
            plan = null;
            if (context?.BlockTable == null || graph == null ||
                root?.Statements.GetCondition() == null)
            {
                return false;
            }

            Expression GetPureCondition(Block block) =>
                block == root || block?.Statements.IsCondition() == true
                    ? block.Statements.GetCondition()?.Condition
                    : null;
            if (!TryAnalyzeTopology(
                    context, graph, root, GetPureCondition, null,
                    out var topology))
            {
                return false;
            }

            var decisions = topology.Decisions.ToHashSet();
            var passthrough = topology.PassthroughBlocks.ToHashSet();
            var terminals = topology.Terminals;
            if (decisions.Count < 2 || terminals.Count != 2 ||
                decisions.Where(block => block != root).Any(block =>
                    block.From.Any(predecessor =>
                        !decisions.Contains(predecessor) &&
                        !passthrough.Contains(predecessor))))
            {
                return false;
            }

            var orderedTerminals = terminals.OrderBy(block => block.Start).ToList();
            var trueTarget = orderedTerminals[0];
            var falseTarget = orderedTerminals[1];
            if (!IsOwnedPayloadLeaf(trueTarget, decisions, passthrough) ||
                !IsOwnedPayloadLeaf(falseTarget, decisions, passthrough))
            {
                return false;
            }

            var trueSuccessors = graph.GetSuccessors(trueTarget);
            var falseSuccessors = graph.GetSuccessors(falseTarget);
            if (trueSuccessors.Count != 1 || falseSuccessors.Count != 1)
            {
                return false;
            }

            var continuation = NormalizeTarget(
                graph, trueSuccessors[0], passthrough);
            if (continuation == null || continuation == root ||
                continuation != NormalizeTarget(
                    graph, falseSuccessors[0], passthrough))
            {
                return false;
            }

            if (!TryBuildPredicate(
                    context, graph, topology, trueTarget, falseTarget,
                    out var predicate))
            {
                return false;
            }

            plan = new DecisionDagBranchPlan
            {
                Root = root,
                TrueTarget = trueTarget,
                FalseTarget = falseTarget,
                Continuation = continuation,
                Predicate = predicate,
                Decisions = decisions.ToArray(),
                PassthroughBlocks = passthrough.ToArray()
            };
            return true;
        }

        /// <summary>
        /// 识别最终选择“提前 return”或“进入正常正文”的纯条件 DAG。
        /// </summary>
        public static bool TryAnalyzeReturnGuard(
            DecompileContext context, ControlFlowGraphAnalysis graph,
            Block root, out DecisionDagReturnPlan plan)
        {
            plan = null;
            if (context?.BlockTable == null || graph == null ||
                root?.Statements.GetCondition() == null)
            {
                return false;
            }

            Expression GetDecisionCondition(Block block)
            {
                var condition = block?.Statements.GetCondition();
                if (condition == null)
                {
                    return null;
                }
                if (block == root || block.Statements.IsCondition())
                {
                    return condition.Condition;
                }

                if (TryBuildStrictAssignmentCondition(
                        context, block, condition, out var expression))
                {
                    return expression;
                }
                return null;
            }

            if (!TryAnalyzeTopology(
                    context, graph, root, GetDecisionCondition, null,
                    out var topology))
            {
                return false;
            }

            var decisions = topology.Decisions.ToHashSet();
            var passthrough = topology.PassthroughBlocks.ToHashSet();
            var terminals = topology.Terminals;
            if (terminals.Count != 2)
            {
                return false;
            }

            var returnTargets = terminals.Where(IsSimpleTerminalReturnBlock).ToList();
            Block returnTarget;
            Block bodyTarget;
            if (returnTargets.Count == 1)
            {
                returnTarget = returnTargets[0];
                bodyTarget = terminals.First(target => target != returnTarget);
            }
            else if (returnTargets.Count == 2)
            {
                // 两端都含 return 时，只有当前 DAG 独占的纯 return 可作为
                // 提前返回；带外部前驱的一端仍是正常正文共享的函数尾部。
                var uniquePureReturns = returnTargets.Where(target =>
                    target.Statements.All(statement =>
                        statement is ReturnExpression or GotoExpression) &&
                    target.From.All(predecessor =>
                        decisions.Contains(predecessor) ||
                        passthrough.Contains(predecessor))).ToList();
                if (uniquePureReturns.Count != 1)
                {
                    return false;
                }

                returnTarget = uniquePureReturns[0];
                bodyTarget = returnTargets.First(target => target != returnTarget);
                if (!bodyTarget.From.Any(predecessor =>
                        !decisions.Contains(predecessor) &&
                        !passthrough.Contains(predecessor)))
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            if (!TryBuildPredicate(
                    context, graph, topology, returnTarget, bodyTarget,
                    out var predicate))
            {
                return false;
            }

            var sharedReturn = returnTarget.From.Any(predecessor =>
                !decisions.Contains(predecessor) &&
                !passthrough.Contains(predecessor));
            // 共享最终 return 属于父级 Sequence。复制一份到提前返回分支会让
            // 后续结构化器仍可能隐藏原块，其他失败路径随后退化为隐式 void。
            // 此类候选交回普通 if 区域恢复，不在 DAG 计划中复制公共出口。
            if (sharedReturn)
            {
                return false;
            }

            plan = new DecisionDagReturnPlan
            {
                Root = root,
                ReturnTarget = returnTarget,
                BodyTarget = bodyTarget,
                Predicate = predicate,
                Decisions = decisions.ToArray(),
                PassthroughBlocks = passthrough.ToArray()
            };
            return true;
        }

        public static bool TryAnalyzePayload(
            DecompileContext context, ControlFlowGraphAnalysis graph,
            Block root, out DecisionDagPlan plan)
        {
            plan = null;
            if (context?.BlockTable == null || graph == null ||
                root?.Statements.GetCondition() == null)
            {
                return false;
            }

            // 直接后支配块是当前源码条件的边界。它本身即使还是条件入口，
            // 也属于父级 Sequence，不能继续纳入当前路径谓词。
            var continuation = graph.FindImmediatePostDominator(root);
            if (continuation == null || continuation == root)
            {
                return false;
            }

            Expression GetPureCondition(Block block) =>
                block == root || block?.Statements.IsCondition() == true
                    ? block.Statements.GetCondition()?.Condition
                    : null;
            if (!TryAnalyzeTopology(
                    context, graph, root, GetPureCondition, continuation,
                    out var topology))
            {
                return false;
            }

            var decisions = topology.Decisions.ToHashSet();
            var passthrough = topology.PassthroughBlocks.ToHashSet();
            var terminals = topology.Terminals;
            if (terminals.Count != 2 || !terminals.Contains(continuation))
            {
                return false;
            }

            var body = terminals.First(block => block != continuation);
            if (!HasPayload(body) || body.Statements.Any(statement =>
                    statement is ReturnExpression or ThrowExpression))
            {
                return false;
            }

            // 此计划只拥有单个叶子载荷。多块正文可能包含独立分支和多次
            // 副作用，必须交给通用区域结构化器处理。
            var bodySuccessors = graph.GetSuccessors(body);
            if (bodySuccessors.Count != 1 || bodySuccessors[0] != continuation ||
                !graph.AllPathsReach(body, continuation))
            {
                return false;
            }

            if (!TryBuildPredicate(
                    context, graph, topology, body, continuation,
                    out var predicate))
            {
                return false;
            }

            plan = new DecisionDagPlan
            {
                Root = root,
                Continuation = continuation,
                Body = body,
                Predicate = predicate,
                Decisions = decisions.ToArray(),
                PassthroughBlocks = passthrough.ToArray()
            };
            return true;
        }

        private static bool TryGetBranch(
            DecompileContext context, int address, out Block block)
        {
            return context.BlockTable.TryGetValue(address, out block);
        }

        private static bool HasPayload(Block block)
        {
            return block?.Statements.Any(node =>
                node is not ConditionExpression && node is not GotoExpression) == true;
        }

        private static bool IsOwnedPayloadLeaf(
            Block block, HashSet<Block> decisions,
            HashSet<Block> passthrough)
        {
            return HasPayload(block) &&
                   !block.Statements.Any(statement =>
                       statement is ConditionExpression or ReturnExpression or
                           ThrowExpression) &&
                   block.From.All(predecessor =>
                       decisions.Contains(predecessor) ||
                       passthrough.Contains(predecessor));
        }

        private static bool IsSimpleTerminalReturnBlock(Block target)
        {
            return target?.Statements.Count > 0 &&
                   target.Statements.Any(statement => statement is ReturnExpression) &&
                   target.Statements.All(statement =>
                       statement is BinaryExpression or UnaryExpression or
                           InvokeExpression or ReturnExpression or GotoExpression);
        }

        /// <summary>
        /// 为候选 DAG 构造 `(target = value) op ...`，不删除原块中的赋值。
        /// 仅局部槽可以这样代换；成员写入后的再读取可能触发 getter，
        /// 必须保留为两个独立的可观察操作。
        /// </summary>
        private static bool TryBuildStrictAssignmentCondition(
            DecompileContext context, Block block,
            ConditionExpression condition, out Expression result)
        {
            result = null;
            if (block?.Statements?.Count != 2 ||
                block.Statements[0] is not BinaryExpression assignment ||
                assignment.Op != BinaryOp.Assign ||
                assignment.Left is not LocalExpression ||
                block.Statements[1] != condition)
            {
                return false;
            }

            // 首次声明不能进入条件表达式。若已有支配声明，则这里只复制成普通
            // 赋值节点，避免修改原 AST 上的 IsDeclaration 状态。
            if (assignment.IsDeclaration &&
                !HasEarlierDominatingDeclaration(context, block, assignment.Left))
            {
                return false;
            }

            var replacement = new BinaryExpression(
                assignment.Left, assignment.Right, assignment.Op)
            {
                IsDeclaration = false,
                IsSelfAssignment = assignment.IsSelfAssignment,
                ResultType = assignment.ResultType
            };
            var replaced = false;
            result = ReplaceTargetCopy(
                condition.Condition, assignment.Left, replacement, ref replaced);
            if (!replaced)
            {
                result = null;
            }
            return replaced;
        }

        private static bool HasEarlierDominatingDeclaration(
            DecompileContext context, Block block, Expression target)
        {
            return context?.Blocks.Any(candidate =>
                candidate != block && candidate.Start < block.Start &&
                block.Dominator != null && block.Dominator[candidate.Id] &&
                candidate.Statements?.OfType<BinaryExpression>().Any(previous =>
                    previous.IsDeclaration && previous.Op == BinaryOp.Assign &&
                    ExpressionStructuralComparer.AreEquivalent(
                        previous.Left, target)) == true) == true;
        }

        /// <summary>
        /// 只复制通向赋值目标的布尔表达式骨架，候选失败时原条件树保持不变。
        /// </summary>
        private static Expression ReplaceTargetCopy(
            Expression expression, Expression target,
            Expression replacement, ref bool replaced)
        {
            if (expression == null || replaced)
            {
                return expression;
            }
            if (ExpressionStructuralComparer.AreEquivalent(expression, target))
            {
                replaced = true;
                return replacement;
            }

            switch (expression)
            {
                case BinaryExpression binary:
                {
                    var left = ReplaceTargetCopy(
                        binary.Left, target, replacement, ref replaced);
                    var right = replaced
                        ? binary.Right
                        : ReplaceTargetCopy(
                            binary.Right, target, replacement, ref replaced);
                    return new BinaryExpression(left, right, binary.Op)
                    {
                        IsDeclaration = binary.IsDeclaration,
                        IsSelfAssignment = binary.IsSelfAssignment,
                        ResultType = binary.ResultType
                    };
                }
                case UnaryExpression unary:
                    return new UnaryExpression(
                        ReplaceTargetCopy(
                            unary.Target, target, replacement, ref replaced),
                        unary.Op)
                    {
                        IsPrefix = unary.IsPrefix,
                        IsSelfAssignment = unary.IsSelfAssignment,
                        ResultType = unary.ResultType
                    };
                default:
                    return expression;
            }
        }

        /// <summary>
        /// 只读穿透空跳板和已经折叠为同一出口的隐藏条件壳。
        /// </summary>
        private static Block NormalizeTarget(
            ControlFlowGraphAnalysis graph, Block block,
            HashSet<Block> passthroughBlocks)
        {
            var memo = new Dictionary<Block, Block>();
            var visiting = new HashSet<Block>();
            Block Normalize(Block current)
            {
                if (current == null || !visiting.Add(current))
                {
                    return current;
                }
                if (memo.TryGetValue(current, out var cached))
                {
                    visiting.Remove(current);
                    return cached;
                }

                var successors = graph.GetSuccessors(current);
                var result = current;
                if (successors.Count == 1 && current.Statements.All(statement =>
                        statement is GotoExpression))
                {
                    passthroughBlocks.Add(current);
                    result = Normalize(successors[0]);
                }
                else if (current.Hidden && successors.Count == 2 &&
                         (current.Statements.Count == 0 ||
                          current.Statements.IsCondition()))
                {
                    var first = Normalize(successors[0]);
                    var second = Normalize(successors[1]);
                    if (first == second)
                    {
                        passthroughBlocks.Add(current);
                        result = first;
                    }
                }

                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Normalize(block);
        }
    }
}
