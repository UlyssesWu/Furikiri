using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 读取 CFG 未保留的编译器收尾跳转，为无法仅凭图形区分的单 case switch
    /// 提供额外来源证据。这里只回答候选是否可信，不构造或隐藏任何区域。
    /// </summary>
    internal static class SwitchCompilationPatternAnalyzer
    {
        internal static void CaptureEvidence(DecompileContext context)
        {
            if (context == null)
            {
                return;
            }
            if (context.BlockTable == null)
            {
                context.UpdateBlockTable();
            }

            context.SingleCaseSwitchRoots.Clear();
            context.CachedEqualityConditionBlocks.Clear();
            context.CachedEqualityConditions.Clear();
            foreach (var block in context.Blocks.Where(
                         EqualityDispatchAnalyzer.HasCachedSelector))
            {
                context.CachedEqualityConditionBlocks.Add(block);
                context.CachedEqualityConditions[block] =
                    block.Statements.GetCondition();
            }
            var candidates = context.Blocks
                .Where(block => IsSingleCaseSwitchCore(context, block))
                .ToHashSet();
            var multiCaseRoots = context.Blocks
                .Where(block => IsMultiCaseDispatchRoot(context, block))
                .ToArray();
            foreach (var block in candidates)
            {
                var dominatedByMultiCaseRoot = multiCaseRoots.Any(parent =>
                    parent != block && block.Dominator != null &&
                    parent.Id >= 0 && parent.Id < block.Dominator.Length &&
                    block.Dominator[parent.Id]);
                if (!BorrowsEnclosingSwitchCleanup(
                        context, block, candidates) &&
                    (!dominatedByMultiCaseRoot ||
                     HasOwnedExplicitDefaultCleanup(context, block)))
                {
                    context.SingleCaseSwitchRoots.Add(block);
                }
            }

            context.HasCapturedSingleCaseSwitchEvidence = true;
        }

        private static bool IsMultiCaseDispatchRoot(
            DecompileContext context, Block root)
        {
            if (root?.Statements.GetCondition()?.Condition is not
                BinaryExpression
                {
                    Op: BinaryOp.Equal or BinaryOp.Congruent
                } comparison ||
                !comparison.Left.CachedEvaluationId.HasValue ||
                !context.BlockTable.TryGetValue(
                    root.Statements.GetCondition().FalseBranch, out var next))
            {
                return false;
            }

            return next.Statements.GetCondition()?.Condition is
                       BinaryExpression
                       {
                           Op: BinaryOp.Equal or BinaryOp.Congruent
                       } nextComparison &&
                   EqualityDispatchAnalyzer.AreSameSelectorEvaluation(
                       comparison.Left, nextComparison.Left);
        }

        /// <summary>
        /// case 正文中的普通 if 可能与外层单 case switch 共用同一个出口，因而
        /// 紧邻外层的收尾 JMP。沿直线前驱找到相同未命中出口的父候选时，收尾
        /// 所有权归父分派，内层条件不能再次用它证明自己也是 switch。
        /// </summary>
        private static bool BorrowsEnclosingSwitchCleanup(
            DecompileContext context, Block root,
            HashSet<Block> candidates)
        {
            var condition = root.Statements.GetCondition();
            if (condition == null)
            {
                return false;
            }

            var pending = new Stack<Block>(root.From);
            var visited = new HashSet<Block>();
            while (pending.Count > 0)
            {
                var predecessor = pending.Pop();
                if (!visited.Add(predecessor) || predecessor.Start >= root.Start)
                {
                    continue;
                }

                var predecessorCondition =
                    predecessor.Statements.GetCondition();
                if (candidates.Contains(predecessor) &&
                    predecessorCondition?.FalseBranch == condition.FalseBranch)
                {
                    return true;
                }

                // 只跨越不分叉的准备/跳板块，避免越过另一条独立决策路径把
                // 更早、恰好同出口的无关 switch 当成父节点。
                if (predecessor.To.Count == 1)
                {
                    foreach (var parent in predecessor.From)
                    {
                        pending.Push(parent);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 嵌套在多 case 分派中的单 case 候选默认不应复用父级收尾。若候选
        /// 正文与自己的未命中入口之间存在两条均直达该入口的不可达 JMP，
        /// 则它拥有独立的 case/default 两级收尾，可以安全恢复为内层 switch。
        /// </summary>
        private static bool HasOwnedExplicitDefaultCleanup(
            DecompileContext context, Block root)
        {
            var condition = root?.Statements.GetCondition();
            if (condition == null || !context.BlockTable.TryGetValue(
                    condition.FalseBranch, out var unmatched) ||
                unmatched.Start <= root.End)
            {
                return false;
            }

            var previousReachable = context.Blocks
                .Where(block => block != root && block.Start > root.End &&
                                block.Start < unmatched.Start)
                .OrderByDescending(block => block.End)
                .FirstOrDefault();
            var gapStart = (previousReachable?.End ?? root.End) + 1;
            var gapLength = unmatched.Start - gapStart;
            if (gapLength < 2 || gapStart < 0 ||
                unmatched.Start > context.Method.Instructions.Count)
            {
                return false;
            }

            var cleanup = context.Method.Instructions
                .Skip(gapStart)
                .Take(gapLength)
                .ToList();
            return cleanup.Count == gapLength && cleanup.All(instruction =>
                instruction.OpCode == OpCode.JMP &&
                instruction.Data is JumpData jump &&
                jump.Goto?.Line == unmatched.Start);
        }

        internal static bool IsSingleCaseSwitch(
            DecompileContext context, Block root)
            => context?.HasCapturedSingleCaseSwitchEvidence == true
                ? context.SingleCaseSwitchRoots.Contains(root)
                : IsSingleCaseSwitchCore(context, root);

        /// <summary>
        /// 判断相等分派的未命中入口是否属于显式 default。
        /// </summary>
        /// <remarks>
        /// 当最后一个 case 以 return/throw 终止时，源码含不含 default 的可达
        /// CFG 形状可能完全相同。编译器仍会为显式 default 生成“case 收尾 +
        /// 分派收尾”两级不可达 JMP；没有 default 时只保留一级。该信息必须
        /// 直接从未改写指令读取，不能用 default 正文内容或方法名推断。
        /// </remarks>
        internal static bool HasExplicitDefaultClause(
            DecompileContext context, EqualityDispatchPlan dispatch)
        {
            if (context?.Method?.Instructions == null || dispatch == null ||
                dispatch.ConditionBlocks.Count == 0 ||
                dispatch.DefaultTarget == null)
            {
                return false;
            }

            var defaultStart = dispatch.DefaultTarget.Start;
            var lastConditionEnd = dispatch.ConditionBlocks[^1].End;
            if (defaultStart <= lastConditionEnd)
            {
                return false;
            }

            // 最后一个标签的正文可能位于末次比较与 default 之间；取 default
            // 前最后一个可达块，检查二者之间被 CFG 丢弃的原始指令。
            var previousReachable = context.Blocks
                .Where(block => block.Start > lastConditionEnd &&
                                block.Start < defaultStart)
                .OrderByDescending(block => block.End)
                .FirstOrDefault();
            var gapStart = (previousReachable?.End ?? lastConditionEnd) + 1;
            var gapLength = defaultStart - gapStart;
            if (gapLength < 2 || gapStart < 0 ||
                defaultStart > context.Method.Instructions.Count)
            {
                return false;
            }

            var cleanup = context.Method.Instructions
                .Skip(gapStart)
                .Take(gapLength)
                .ToList();
            return cleanup.Count == gapLength && cleanup.All(instruction =>
                instruction.OpCode == OpCode.JMP &&
                instruction.Data is JumpData jump &&
                jump.Goto?.Line == defaultStart);
        }

        private static bool IsSingleCaseSwitchCore(
            DecompileContext context, Block root)
        {
            if (context?.Method?.Instructions == null || root == null ||
                root.Statements.GetCondition()?.Condition is not BinaryExpression
                {
                    Op: BinaryOp.Equal or BinaryOp.Congruent
                } comparison ||
                comparison.Left?.CachedTemporarySlot.HasValue != true ||
                comparison.Left.CachedEvaluationId.HasValue != true)
            {
                return false;
            }

            var condition = root.Statements.GetCondition();
            if (!context.BlockTable.TryGetValue(
                    condition.FalseBranch, out var unmatched))
            {
                return false;
            }

            if (unmatched.Start <= root.End)
            {
                return IsBackwardExitSingleCaseSwitch(
                    context, root, condition, unmatched);
            }

            // 单 case 的来源只能依赖当前分派自己的收尾空洞。若命中正文内还
            // 含有一条独立的缓存相等分派，空洞可能属于内层 switch；此时仅
            // 凭指令间距无法证明外层也是 switch，应保守地保留为普通 if。
            // 这也避免内层结构被递归恢复后，反过来污染父候选的来源判断。
            if (ContainsNestedEqualityDispatch(context, root, unmatched))
            {
                return false;
            }

            // switch 的 case break/return 后会生成一小段只含 JMP 的收尾代码，
            // CFG 因其不可达而跳过，于是相邻可达块的指令行号之间留下空洞。
            // 普通单个 if 即使比较同一个缓存表达式，也会直接邻接 false 分支。
            var previousReachable = context.Blocks
                .Where(block => block != root &&
                                block.Start > root.Start &&
                                block.Start < unmatched.Start)
                .OrderByDescending(block => block.End)
                .FirstOrDefault();
            var previousReachableEnd = previousReachable?.End ?? root.End;
            var gapStart = previousReachableEnd + 1;
            var gapLength = unmatched.Start - gapStart;
            // 单个不可达 JMP 也常由 while 退出或“if 后 return”产生；switch
            // 在 case 收尾和分派收尾之间至少留下两条连续跳转。证据不足时宁可
            // 保留 if，避免用追求召回率的猜测制造伪 switch。
            if (gapLength < 1 || gapStart < 0 ||
                unmatched.Start > context.Method.Instructions.Count)
            {
                return false;
            }

            var gap = context.Method.Instructions
                .Skip(gapStart)
                .Take(gapLength)
                .ToList();
            if (gap.Count != gapLength || gap.Any(instruction =>
                    instruction.OpCode != OpCode.JMP))
            {
                return false;
            }

            // 两条以上的空洞是 case 与分派两级收尾。单 case + break 只留下
            // 一条不可达 JMP，此时还要求命中正文的最后一条可达指令本身也是
            // 指向公共出口的 JMP；普通 if 的正文通常会直接顺序落入继续点。
            var hasForwardCleanup = gap.Any(instruction =>
                instruction.Data is JumpData jump &&
                jump.Goto != null && jump.Goto.Line >= unmatched.Start);
            if (!hasForwardCleanup)
            {
                return false;
            }

            if (gapLength >= 2)
            {
                return true;
            }

            var reachableTail = previousReachable != null &&
                previousReachable.End >= 0 &&
                previousReachable.End < context.Method.Instructions.Count
                    ? context.Method.Instructions[previousReachable.End]
                    : null;
            return reachableTail?.OpCode == OpCode.JMP &&
                   reachableTail.Data is JumpData reachableJump &&
                   reachableJump.Goto != null &&
                   reachableJump.Goto.Line >= unmatched.Start;
        }

        /// <summary>
        /// 循环尾 switch 的 default/break 会直接回到循环头，未命中目标因而位于
        /// 条件之前。收集命中臂的前向区域后，仅当它也回到同一出口，且区域后
        /// 至少有两条只指向该出口的不可达 JMP，才认定为单 case switch。
        /// </summary>
        private static bool IsBackwardExitSingleCaseSwitch(
            DecompileContext context, Block root,
            ConditionExpression condition, Block unmatched)
        {
            if (!context.BlockTable.TryGetValue(
                    condition.TrueBranch, out var body) ||
                body.Start <= root.End)
            {
                return false;
            }

            var region = new HashSet<Block>();
            var pending = new Stack<Block>();
            pending.Push(body);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == unmatched || current.Start <= root.End)
                {
                    continue;
                }

                if (!region.Add(current))
                {
                    continue;
                }

                foreach (var successor in current.To)
                {
                    if (successor != unmatched)
                    {
                        pending.Push(successor);
                    }
                }
            }

            if (region.Count == 0 || region.Any(block => block.To.Any(
                    successor => successor != unmatched &&
                                 !region.Contains(successor))))
            {
                return false;
            }

            var regionEnd = region.Max(block => block.End);
            var nextReachable = context.Blocks
                .Where(block => block.Start > regionEnd)
                .OrderBy(block => block.Start)
                .FirstOrDefault();
            if (nextReachable == null || nextReachable.Start - regionEnd - 1 < 2)
            {
                return false;
            }

            var cleanup = context.Method.Instructions
                .Skip(regionEnd + 1)
                .Take(nextReachable.Start - regionEnd - 1)
                .ToList();
            return cleanup.Count >= 2 && cleanup.All(instruction =>
                instruction.OpCode == OpCode.JMP &&
                instruction.Data is JumpData jump &&
                jump.Goto?.Line == unmatched.Start);
        }

        private static bool ContainsNestedEqualityDispatch(
            DecompileContext context, Block root, Block unmatched)
        {
            var selectors = new List<Expression>();
            foreach (var block in context.Blocks.Where(block =>
                         block != root && block.Start > root.End &&
                         block.Start < unmatched.Start))
            {
                if (block.Statements.GetCondition()?.Condition is not
                    BinaryExpression
                    {
                        Op: BinaryOp.Equal or BinaryOp.Congruent
                    } comparison ||
                    !comparison.Left.CachedEvaluationId.HasValue)
                {
                    continue;
                }

                if (selectors.Any(selector =>
                        EqualityDispatchAnalyzer.AreSameSelectorEvaluation(
                            selector, comparison.Left)))
                {
                    return true;
                }

                selectors.Add(comparison.Left);
            }

            return false;
        }
    }
}
