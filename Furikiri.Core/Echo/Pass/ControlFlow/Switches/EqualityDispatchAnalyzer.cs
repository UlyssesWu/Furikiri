using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 相等分派中的一组 case。多个比较值可以指向同一个真实正文入口。
    /// </summary>
    internal sealed class EqualityDispatchGroup
    {
        public Block Target { get; set; }
        public Block Owner { get; set; }
        public List<Expression> Conditions { get; } = new List<Expression>();
    }

    /// <summary>
    /// 只读分析相等比较链得到的结构计划；本对象不隐藏基本块，也不生成 AST。
    /// </summary>
    internal sealed class EqualityDispatchPlan
    {
        public Expression ComparisonTarget { get; set; }
        public Block DefaultTarget { get; set; }
        public HashSet<Block> Labels { get; } = new HashSet<Block>();
        public List<Block> ConditionBlocks { get; } = new List<Block>();
        public List<EqualityDispatchGroup> Groups { get; } =
            new List<EqualityDispatchGroup>();
    }

    /// <summary>
    /// 识别编译器生成的连续相等比较分派。
    /// </summary>
    /// <remarks>
    /// 分析阶段只回答“哪些条件属于同一分派、各自进入哪个正文”，不进行区域
    /// 收集和语句物化。这样拒绝一个候选时不会留下半隐藏块或已改写条件，后续
    /// 普通 if/短路恢复仍能看到完整 CFG。
    /// </remarks>
    internal static class EqualityDispatchAnalyzer
    {
        /// <summary>
        /// 判断两个选择表达式是否来自同一次 VM 求值。工作表可能为同一条
        /// 指令重建 AST 节点，因此不能只比较对象引用；反过来，相同临时槽
        /// 也会被后续指令复用，必须同时核对定义指令身份。
        /// </summary>
        internal static bool AreSameSelectorEvaluation(
            Expression left, Expression right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            if (left.CachedEvaluationId.HasValue ||
                right.CachedEvaluationId.HasValue)
            {
                return left.CachedEvaluationId.HasValue &&
                       left.CachedEvaluationId == right.CachedEvaluationId;
            }

            if (left is UnaryExpression || right is UnaryExpression)
            {
                return false;
            }

            return ExpressionStructuralComparer.AreEquivalent(left, right);
        }

        /// <summary>
        /// 判断当前比较是否与相邻块组成同一条缓存值分派链。终止叶和早退守卫
        /// 应把这种候选留给完整 equality 区域，避免从尾部逐项拆散 switch。
        /// 普通标识符比较仍按原有顺序处理，防止保护范围扩张到源码中的独立
        /// 状态检查；这里只接受具有相同定义指令身份的选择值。
        /// </summary>
        public static bool IsPartOfDispatchChain(Block block)
        {
            if (!TryGetComparisonTarget(block, out var selector) ||
                !selector.CachedEvaluationId.HasValue)
            {
                return false;
            }

            return block.From.Concat(block.To)
                .Where(neighbor => neighbor != block)
                .Any(neighbor =>
                    TryGetComparisonTarget(neighbor, out var neighborSelector) &&
                    neighborSelector.CachedEvaluationId.HasValue &&
                    AreSameSelectorEvaluation(selector, neighborSelector));
        }

        /// <summary>
        /// 当前比较是否使用了明确来自 VM 临时结果的选择值。只有这种分派才
        /// 需要抢在普通 if 之前物化；普通局部变量的相等链仍交给常规区域顺序。
        /// </summary>
        public static bool HasCachedSelector(Block block)
            => TryGetComparisonTarget(block, out var selector) &&
               selector.CachedEvaluationId.HasValue;

        /// <summary>
        /// 判断终止叶的一侧是否其实是前一个相等 case 共用的正文。
        /// 编译器常把前一标签先跳到公共正文，再让最后一个比较直接进入同一块；
        /// 此时最后一个比较不能先被恢复成普通双 return 叶，否则公共正文会被
        /// 当成继续点并从整条分派中丢失。
        /// </summary>
        public static bool IsSharedCaseBody(
            Block conditionBlock, Block possibleBody)
        {
            if (!TryGetComparisonTarget(conditionBlock, out var selector) ||
                possibleBody == null)
            {
                return false;
            }

            foreach (var predecessor in possibleBody.From.Where(
                         block => block != conditionBlock))
            {
                IEnumerable<Block> owners;
                if (predecessor.To.Count == 1 &&
                    predecessor.To[0] == possibleBody &&
                    predecessor.Statements.All(statement =>
                        statement is GotoExpression))
                {
                    owners = predecessor.From;
                }
                else
                {
                    owners = new[] { predecessor };
                }

                foreach (var owner in owners)
                {
                    var entersSharedBody = owner.To.Contains(predecessor) ||
                                           owner.To.Contains(possibleBody);
                    if (owner == conditionBlock || !entersSharedBody ||
                        !TryGetComparisonTarget(owner, out var ownerSelector))
                    {
                        continue;
                    }

                    if (AreSameSelectorEvaluation(selector, ownerSelector))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetComparisonTarget(
            Block block, out Expression selector)
        {
            selector = null;
            if (block?.Statements.GetCondition()?.Condition is not BinaryExpression
                {
                    Op: BinaryOp.Equal or BinaryOp.Congruent
                } comparison)
            {
                return false;
            }

            selector = comparison.Left;
            return true;
        }

        public static bool TryAnalyze(
            DecompileContext context, Block root,
            Func<Block, HashSet<Block>, Expression, Block> normalizeTarget,
            out EqualityDispatchPlan plan)
        {
            plan = null;
            if (context?.BlockTable == null || root == null || normalizeTarget == null)
            {
                return false;
            }

            var candidate = new EqualityDispatchPlan();
            var comparisons = new List<BinaryExpression>();
            var current = root;
            while (current != null)
            {
                var condition = GetCondition(context, current);
                if (condition?.Condition is not BinaryExpression comparison ||
                    comparison.Op is not (BinaryOp.Equal or BinaryOp.Congruent))
                {
                    candidate.DefaultTarget = current;
                    break;
                }

                if (candidate.ComparisonTarget == null)
                {
                    candidate.ComparisonTarget = comparison.Left;
                    // 分派计划会跨越多个 CFG/AST 阶段，必须由同一次 VM 定义
                    // 身份证明选择值只求值一次。仅仅结构相同的局部读取也可能
                    // 来自顺序 if 链；把过滤留给调用者会使其他入口绕过不变量。
                    if (!candidate.ComparisonTarget.CachedEvaluationId.HasValue)
                    {
                        return false;
                    }
                }
                else if (!AreSameSelectorEvaluation(
                             candidate.ComparisonTarget, comparison.Left))
                {
                    candidate.DefaultTarget = current;
                    break;
                }

                // 同一分派不会重复比较完全相同的“目标 + 值”。重复项表示前一条
                // 路径可能修改了目标，后面的比较属于顺序重检，不能改写成 else-if。
                if (comparisons.Any(previous =>
                        ExpressionStructuralComparer.AreEquivalent(previous, comparison)))
                {
                    return false;
                }
                comparisons.Add(comparison);

                if (!context.BlockTable.TryGetValue(
                        condition.TrueBranch, out var bodyTarget) ||
                    !context.BlockTable.TryGetValue(
                        condition.FalseBranch, out var nextTarget))
                {
                    return false;
                }

                bodyTarget = normalizeTarget(
                    bodyTarget, candidate.Labels, candidate.ComparisonTarget);
                nextTarget = normalizeTarget(
                    nextTarget, candidate.Labels, candidate.ComparisonTarget);
                candidate.ConditionBlocks.Add(current);

                var group = candidate.Groups.FirstOrDefault(entry =>
                    entry.Target == bodyTarget);
                if (group == null)
                {
                    group = new EqualityDispatchGroup
                    {
                        Target = bodyTarget,
                        Owner = current
                    };
                    candidate.Groups.Add(group);
                }
                group.Conditions.Add(comparison);

                var nextCondition = GetCondition(context, nextTarget);
                if (nextCondition?.Condition is BinaryExpression nextComparison &&
                    nextComparison.Op is BinaryOp.Equal or BinaryOp.Congruent &&
                    AreSameSelectorEvaluation(
                        nextComparison.Left, candidate.ComparisonTarget))
                {
                    current = nextTarget;
                    continue;
                }

                candidate.DefaultTarget = nextTarget;
                break;
            }

            var minimumComparisonCount =
                SwitchCompilationPatternAnalyzer.IsSingleCaseSwitch(context, root)
                    ? 1
                    : 2;
            if (candidate.ConditionBlocks.Count < minimumComparisonCount ||
                candidate.Groups.Count == 0 || candidate.DefaultTarget == null)
            {
                return false;
            }

            plan = candidate;
            return true;
        }

        private static ConditionExpression GetCondition(
            DecompileContext context, Block block)
        {
            var live = block?.Statements.GetCondition();
            if (live != null)
            {
                return live;
            }

            return block != null &&
                   context.CachedEqualityConditions.TryGetValue(
                       block, out var frozen)
                ? frozen
                : null;
        }

    }
}
