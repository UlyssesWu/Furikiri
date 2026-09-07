using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    internal enum TerminalGuardKind
    {
        Return,
        Throw
    }

    /// <summary>
    /// 单个条件根到独占终止臂的只读区域计划。
    /// </summary>
    internal sealed class TerminalGuardPlan
    {
        public Block Root { get; set; }
        public ConditionExpression OriginalCondition { get; set; }
        public Expression Predicate { get; set; }
        public Block Terminal { get; set; }
        public Block Continuation { get; set; }
        public TerminalGuardKind Kind { get; set; }
        public bool IsPureTerminal { get; set; }
    }

    /// <summary>
    /// 旧结构化流程中仍需提前处理的终止守卫阶段。
    /// </summary>
    /// <remarks>
    /// 阶段只表示迁移期间的调度时机，候选本身统一由同一个拓扑分析器产生。
    /// 后续区域调度器接管重叠候选后可删除这些阶段枚举。
    /// </remarks>
    internal enum TerminalGuardStage
    {
        Throw,
        SharedInvocation,
        UpdatedValue,
        BeforeCondition
    }

    /// <summary>
    /// 根据 CFG 邻接和终点所有权发现提前返回/抛出守卫，不修改 AST 或基本块。
    /// </summary>
    internal static class TerminalGuardPlanner
    {
        public static IReadOnlyList<TerminalGuardPlan> Create(
            DecompileContext context, TerminalGuardStage stage)
        {
            if (context == null)
            {
                return Array.Empty<TerminalGuardPlan>();
            }

            var plans = context.Blocks
                .Where(block => !block.Hidden && block.To.Count == 2 &&
                                block.Statements.GetCondition() != null &&
                                !context.LoopSet.Any(loop => loop.Contains(block)))
                .Select(block => TryAnalyze(block, out var plan) ? plan : null)
                .Where(plan => plan != null)
                .ToList();

            IEnumerable<TerminalGuardPlan> selected = stage switch
            {
                TerminalGuardStage.Throw => plans.Where(plan =>
                    plan.Kind == TerminalGuardKind.Throw),
                TerminalGuardStage.SharedInvocation => SelectSharedInvocationGuards(plans),
                TerminalGuardStage.UpdatedValue => plans.Where(plan =>
                    plan.Kind == TerminalGuardKind.Return &&
                    plan.IsPureTerminal &&
                    HasLeadingLocalUpdate(plan.Root) &&
                    ReturnsConstant(plan.Terminal)),
                TerminalGuardStage.BeforeCondition => plans.Where(plan =>
                    plan.Kind == TerminalGuardKind.Return &&
                    plan.IsPureTerminal &&
                    plan.Predicate is not BinaryExpression
                    {
                        Op: BinaryOp.LogicAnd or BinaryOp.LogicOr
                    } &&
                    plan.Continuation.Statements.GetCondition() != null &&
                    plan.Continuation.To.Any(IsPureReturn) &&
                    !EqualityDispatchAnalyzer.IsPartOfDispatchChain(plan.Root)),
                _ => Array.Empty<TerminalGuardPlan>()
            };

            return (stage == TerminalGuardStage.BeforeCondition
                    ? selected.OrderBy(plan => plan.Root.Start)
                    : selected.OrderByDescending(plan => plan.Root.Start))
                .ToArray();
        }

        /// <summary>
        /// 仅根据一个条件根判断是否存在恰好一个独占的纯终止臂。
        /// </summary>
        internal static bool TryAnalyze(Block root, out TerminalGuardPlan plan)
        {
            plan = null;
            var condition = root?.Statements?.GetCondition();
            if (condition == null || root.To.Count != 2)
            {
                return false;
            }

            var terminals = root.To
                .Select(target =>
                {
                    var kind = ClassifyTerminal(target);
                    return (Target: target, Kind: kind);
                })
                .Where(candidate => candidate.Kind != null &&
                                    candidate.Target.From.All(predecessor =>
                                        predecessor == root))
                .ToList();
            if (terminals.Count != 1)
            {
                return false;
            }

            var terminal = terminals[0];
            var continuation = root.To.First(target => target != terminal.Target);
            plan = new TerminalGuardPlan
            {
                Root = root,
                OriginalCondition = condition,
                Predicate = terminal.Target.Start == condition.TrueBranch
                    ? condition.Condition
                    : condition.Condition.Invert(),
                Terminal = terminal.Target,
                Continuation = continuation,
                Kind = terminal.Kind.Value,
                IsPureTerminal = IsPureTerminalBlock(terminal.Target,
                    terminal.Kind.Value)
            };
            return true;
        }

        private static IEnumerable<TerminalGuardPlan> SelectSharedInvocationGuards(
            IEnumerable<TerminalGuardPlan> plans)
        {
            // 单个调用条件可能只是较长短路表达式的末项。只有多个嵌套调用守卫
            // 汇入同一继续点时才需要提前物化，避免父谓词复制调用。
            return plans.Where(plan =>
                    plan.Kind == TerminalGuardKind.Return &&
                    plan.IsPureTerminal &&
                    plan.Root.Statements.IsCondition() &&
                    plan.Root.From.Any(predecessor =>
                        predecessor.Statements.GetCondition() != null) &&
                    plan.OriginalCondition.Condition is InvokeExpression)
                .GroupBy(plan => plan.Continuation)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group);
        }

        private static TerminalGuardKind? ClassifyTerminal(Block block)
        {
            if (IsSimpleReturn(block))
            {
                return TerminalGuardKind.Return;
            }
            if (block?.Statements.Count > 0 &&
                block.Statements.Any(statement => statement is ThrowExpression) &&
                block.Statements.All(statement =>
                    statement is ThrowExpression or GotoExpression))
            {
                return TerminalGuardKind.Throw;
            }
            return null;
        }

        private static bool IsSimpleReturn(Block block)
        {
            return block?.Statements.Count > 0 &&
                   block.Statements.Any(statement => statement is ReturnExpression) &&
                   block.Statements.All(statement =>
                       statement is BinaryExpression or UnaryExpression or
                           InvokeExpression or ReturnExpression or GotoExpression);
        }

        private static bool IsPureReturn(Block block)
        {
            return block?.Statements.Count > 0 &&
                   block.Statements.Any(statement => statement is ReturnExpression) &&
                   block.Statements.All(statement =>
                       statement is ReturnExpression or GotoExpression);
        }

        private static bool IsPureTerminalBlock(
            Block block, TerminalGuardKind kind)
        {
            return kind == TerminalGuardKind.Return
                ? IsPureReturn(block)
                : block?.Statements.Count > 0 &&
                  block.Statements.Any(statement => statement is ThrowExpression) &&
                  block.Statements.All(statement =>
                      statement is ThrowExpression or GotoExpression);
        }

        private static bool HasLeadingLocalUpdate(Block block)
        {
            return block.Statements.Any(statement =>
                statement is BinaryExpression binary &&
                (binary.IsSelfAssignment || binary.Op.CanSelfAssign()) &&
                binary.Left is LocalExpression);
        }

        private static bool ReturnsConstant(Block block)
        {
            return block.Statements.OfType<ReturnExpression>()
                .Any(expression => expression.Return is ConstantExpression);
        }
    }

    /// <summary>
    /// 提交已经采用的单终止臂计划。
    /// </summary>
    internal static class TerminalGuardMaterializer
    {
        public static IfLogic Materialize(TerminalGuardPlan plan)
        {
            return new IfLogic
            {
                ConditionBlock = plan.Root,
                Condition = plan.Predicate,
                PostDominator = plan.Continuation,
                Then =
                {
                    Type = LogicalBlockType.BlockList,
                    Blocks = new List<Block> { plan.Terminal }
                },
                Else = { Type = LogicalBlockType.None }
            };
        }
    }
}
