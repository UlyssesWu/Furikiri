using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 终止条件链在控制流主流程中的调度阶段。
    /// 两类候选的所有权规则不同，因此保留明确的阶段边界，而不依赖若干
    /// Where 条件在调用点碰巧互斥。
    /// </summary>
    internal enum TerminalConditionChainStage
    {
        PureCondition,
        EntryPayload
    }

    /// <summary>
    /// 终止条件链的一次只读调度快照。计划只保存识别阶段已经证明的事实，
    /// 不隐藏基本块，也不改写条件；实际取得区域所有权仍由物化阶段完成。
    /// </summary>
    internal sealed class TerminalConditionChainCandidate
    {
        public Block Root { get; init; }
        public TerminalConditionChainStage Stage { get; init; }
        public bool HasPrefixSideEffect { get; init; }
    }

    /// <summary>
    /// 集中管理终止条件链的候选筛选和稳定顺序。
    ///
    /// 这里刻意不尝试生成 IfLogic：候选计划只回答“应由哪一阶段先尝试”。
    /// 路径谓词、区域所有权和回滚仍由各自的分析器负责，避免调度层反向依赖
    /// AST 物化细节。
    /// </summary>
    internal static class TerminalConditionChainPlanner
    {
        public static IReadOnlyList<TerminalConditionChainCandidate> Create(
            DecompileContext context, TerminalConditionChainStage stage)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var candidates = context.Blocks
                .Where(block => IsCandidate(context, block, stage))
                .OrderBy(block => block.Start)
                .Select(block => new TerminalConditionChainCandidate
                {
                    Root = block,
                    Stage = stage,
                    HasPrefixSideEffect = stage == TerminalConditionChainStage.EntryPayload &&
                        block.Statements
                            .Take(block.Statements.Count - 1)
                            .OfType<Expression>()
                            .Any(ExpressionEffectAnalysis
                                .HasObservableEffectBeyondLocalInitialization)
                })
                .ToList();
            return candidates.AsReadOnly();
        }

        private static bool IsCandidate(
            DecompileContext context, Block block, TerminalConditionChainStage stage)
        {
            if (block.Hidden || block.To.Count != 2 ||
                context.LoopSet.Any(loop => loop.Contains(block)))
            {
                return false;
            }

            return stage switch
            {
                TerminalConditionChainStage.PureCondition =>
                    block.Statements.IsCondition() &&
                    HasSinglePureReturnConditionExit(block),
                TerminalConditionChainStage.EntryPayload =>
                    (block.From.Count == 0 ||
                     IsEntryAfterInitializationDiamond(block)) &&
                    !block.Statements.IsCondition() &&
                    block.Statements.GetCondition() != null &&
                    block.To.Any(IsSimpleTerminalReturnBlock),
                _ => false
            };
        }

        /// <summary>
        /// 默认参数初始化会形成一个或多个相连的小菱形。只沿严格的
        /// “条件入口 -> 单赋值块 -> 汇合”形态回溯，防止把普通多前驱块误当入口。
        /// </summary>
        private static bool IsEntryAfterInitializationDiamond(Block block)
        {
            if (block == null || block.From.Count == 0)
            {
                return false;
            }

            var visiting = new HashSet<Block>();
            bool Visit(Block merge)
            {
                if (merge.From.Count == 0)
                {
                    return true;
                }

                if (merge.From.Count != 2 || !visiting.Add(merge))
                {
                    return false;
                }

                return merge.From.Any(entry =>
                {
                    if (entry.To.Count != 2 || !entry.To.Contains(merge) ||
                        (entry.Statements.GetCondition() == null &&
                         !entry.Statements.Any(statement => statement is IfStatement)))
                    {
                        return false;
                    }

                    var initializer = entry.To.First(target => target != merge);
                    return merge.From.Contains(initializer) &&
                           initializer.To.Count == 1 &&
                           initializer.To[0] == merge &&
                           initializer.Statements.Any(statement =>
                               statement is BinaryExpression) &&
                           initializer.Statements.All(statement =>
                               statement is BinaryExpression or GotoExpression) &&
                           Visit(entry);
                });
            }

            return Visit(block);
        }

        internal static bool IsSimpleTerminalReturnBlock(Block target)
        {
            return target?.Statements.Count > 0 &&
                   target.Statements.Any(statement => statement is ReturnExpression) &&
                   target.Statements.All(statement =>
                       statement is BinaryExpression or UnaryExpression or
                           InvokeExpression or ReturnExpression or GotoExpression);
        }

        private static bool HasSinglePureReturnConditionExit(Block root)
        {
            var pending = new Stack<Block>();
            var visited = new HashSet<Block>();
            var terminals = new HashSet<Block>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current != root && current.Statements.GetCondition() == null &&
                    !IsDecisionPassthrough(current))
                {
                    terminals.Add(current);
                    continue;
                }

                foreach (var target in current.To)
                {
                    pending.Push(target);
                }
            }

            if (terminals.Count != 2)
            {
                return false;
            }

            var pureReturns = terminals.Count(block =>
                block.Statements.Count > 0 &&
                block.Statements.All(statement =>
                    statement is ReturnExpression or GotoExpression) &&
                block.Statements.Any(statement => statement is ReturnExpression));
            return pureReturns == 1;
        }

        private static bool IsDecisionPassthrough(Block block)
        {
            return block != null && block.To.Count == 1 &&
                   block.Statements.All(statement => statement is GotoExpression);
        }

    }
}
