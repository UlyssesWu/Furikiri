using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        private Block FindIfPostDominator(Block conditionBlock)
        {
            return _graph.FindImmediatePostDominator(conditionBlock);
        }

        private bool CanReachReturnBefore(Block start, Block stop)
        {
            return _graph.CanReachMatchingBefore(
                start, stop,
                block => block.Statements.Any(statement =>
                    statement is ReturnExpression));
        }

        /// <summary>
        /// 判断从入口出发的每条路径是否都会在到达公共继续块前结束函数。
        /// 终止分支可能先执行若干副作用，再汇到单独的 return 块，不能只检查
        /// 入口块本身是否含 return；遇到环则保守返回 false。
        /// </summary>
        private bool AllPathsTerminateBefore(Block start, Block stop)
        {
            return _graph.AllPathsTerminateBefore(
                start, stop,
                block => block.Statements.Any(statement =>
                    statement is ReturnExpression or ThrowExpression));
        }

        /// <summary>
        /// 判断每条路径是否都会到达指定公共后继，或在到达前以 return/throw 结束。
        /// 图论上的后支配点会被提前返回推到函数出口，但源码结构仍可能有一个
        /// 正常流继续块；只有存在未终止且绕过继续块的路径时才应拒绝该候选。
        /// </summary>
        private bool AllPathsReachOrTerminateAt(Block start, Block stop, Loop loop = null)
        {
            return _graph.AllPathsReachOrTerminateAt(
                start, stop,
                block => block.Statements.Any(statement =>
                    statement is ReturnExpression or ThrowExpression),
                loop == null
                    ? null
                    : block => block == loop.Header || IsContinueTarget(block, loop));
        }

        private static bool IsDecisionPassthrough(Block block)
        {
            return block != null && block.To.Count == 1 &&
                   block.Statements.All(statement => statement is GotoExpression);
        }

        private Block NormalizeDecisionTarget(
            Block block, HashSet<Block> passthroughBlocks, bool includeContinue = false)
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

                Block result = current;
                if ((IsDecisionPassthrough(current) ||
                     includeContinue && IsDecisionContinuePassthrough(current)) &&
                    !_context.CachedEqualityConditionBlocks.Contains(current))
                {
                    passthroughBlocks?.Add(current);
                    result = Normalize(current.To[0]);
                }
                else if (current.Hidden && current.To.Count == 2 &&
                         (current.Statements.Count == 0 ||
                          current.Statements.IsCondition() &&
                          !EqualityDispatchAnalyzer.HasCachedSelector(current) &&
                          !_context.CachedEqualityConditionBlocks.Contains(current)))
                {
                    // `default: case X:` 会让该比较的真假边落到同一正文，但比较
                    // 仍承载 case X 的标签身份。普通跳板归一化不能因目标相同而
                    // 折叠具有缓存选择值定义身份的相等条件。
                    var normalizedFirst = Normalize(current.To[0]);
                    var normalizedSecond = Normalize(current.To[1]);
                    if (normalizedFirst == normalizedSecond)
                    {
                        passthroughBlocks?.Add(current);
                        result = normalizedFirst;
                    }
                }

                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Normalize(block);
        }

        private Block NormalizeConditionChainTarget(Block block, HashSet<Block> passthroughBlocks)
        {
            block = NormalizeConsumedConditionalPhiTarget(block);
            return NormalizeDecisionTarget(block, passthroughBlocks, includeContinue: true);
        }

        private bool IsDecisionContinuePassthrough(Block block)
        {
            if (block == null || block.To.Count != 1 || block.Statements.Count != 1 ||
                block.Statements[0] is not ContinueStatement)
                return false;

            // Loop transfer recovery has already replaced the jump with `continue`.
            // Equivalent exits must still share one decision terminal; otherwise
            // an OR guard can become two unrelated arms and swallow the shared tail.
            var loop = _context.LoopSet.Where(candidate => candidate.Contains(block))
                .OrderBy(candidate => candidate.Blocks.Count).FirstOrDefault();
            return loop != null && IsContinueTarget(block.To[0], loop);
        }

        /// <summary>
        /// 相等分派只保护与当前选择值来自同一次 VM 求值的比较块。
        /// </summary>
        /// <remarks>
        /// case 正文中也可能出现文本相同的相等比较，例如用选择变量构造三元值。
        /// 若仅凭“曾缓存过相等条件”就停止穿透，已经折叠进 Phi 的正文条件壳会
        /// 被误当成外层 case 标签，随后因 Hidden 过滤而丢失整个正文。
        /// </remarks>
        private Block NormalizeEqualityDispatchTarget(
            Block block, HashSet<Block> passthroughBlocks,
            Expression dispatchSelector)
        {
            var memo = new Dictionary<Block, Block>();
            var visiting = new HashSet<Block>();

            bool IsCurrentDispatchComparison(Block candidate)
            {
                var condition = candidate?.Statements.GetCondition();
                if (condition == null && candidate != null)
                {
                    _context.CachedEqualityConditions.TryGetValue(
                        candidate, out condition);
                }

                return condition?.Condition is BinaryExpression
                       {
                           Op: BinaryOp.Equal or BinaryOp.Congruent
                       } comparison &&
                       EqualityDispatchAnalyzer.AreSameSelectorEvaluation(
                           comparison.Left, dispatchSelector);
            }

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

                Block result = current;
                if (IsDecisionPassthrough(current) &&
                    !IsCurrentDispatchComparison(current))
                {
                    passthroughBlocks?.Add(current);
                    result = Normalize(current.To[0]);
                }
                else if (current.Hidden && current.To.Count == 2 &&
                         (current.Statements.Count == 0 ||
                          current.Statements.IsCondition()) &&
                         !IsCurrentDispatchComparison(current))
                {
                    var normalizedFirst = Normalize(current.To[0]);
                    var normalizedSecond = Normalize(current.To[1]);
                    if (normalizedFirst == normalizedSecond)
                    {
                        passthroughBlocks?.Add(current);
                        result = normalizedFirst;
                    }
                }

                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Normalize(block);
        }

        private bool CanReachThroughConditionBlocks(Block start, Block target)
        {
            var pending = new Stack<Block>();
            var visited = new HashSet<Block>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current == target)
                {
                    return true;
                }

                // ExpressionPass 会把短路右值合并进后继条件，留下“无 AST 语句但
                // 仍含求值指令”的单后继壳。路径分析必须与 NormalizeDecisionTarget
                // 使用同一跳板定义，否则不同阶段会对同一 CFG 边界得出相反结论。
                if (!current.Statements.IsCondition() &&
                    current.Statements.Count != 0 &&
                    !IsDecisionPassthrough(current) && !IsDecisionContinuePassthrough(current))
                {
                    continue;
                }

                foreach (var next in current.To)
                {
                    if (next == target || next.Statements.IsCondition() ||
                        next.Statements.Count == 0 || IsDecisionPassthrough(next) ||
                        IsDecisionContinuePassthrough(next))
                    {
                        pending.Push(next);
                    }
                }
            }

            return false;
        }

        private bool CanReach(Block start, Block target)
        {
            return _graph.CanReach(start, target);
        }

        private bool CanReachWithoutLoopBackEdge(Block start, Block target, Block loopHeader)
        {
            // 仅屏蔽所属自然循环的回边；子循环的头块仍保留在图快照中。
            return _graph.CanReachWithoutEntering(start, target, loopHeader);
        }

        private void RemoveLastGoto(Block from, Block to)
        {
            // 调用者传入的是已经证明的结构化汇合点。只移除确实跳到该点的
            // 末尾跳转；忽略目标会把循环 break、continue 或外层区域跳转一起删掉。
            if (from?.Statements.LastOrDefault() is GotoExpression jump &&
                to != null && jump.JumpTo == to.Start)
            {
                from.Statements.RemoveAt(from.Statements.Count - 1);
            }
        }

        /// <summary>
        /// 通过 BFS 穿越隐藏块查找下一个可见延续块。
        /// 当 if/try 等结构化操作隐藏了中间块后，直接后继可能全部不可见，
        /// 需要沿着隐藏块的后继链传递，直到找到第一个可见的延续块。
        /// </summary>
        private Block FindVisibleContinuation(Block currentBlock, HashSet<int> visited, Block thenBlock, Block elseBlock)
        {
            var queue = new Queue<Block>();
            var seen = new HashSet<int>(visited);

            foreach (var t in currentBlock.To)
            {
                if (!seen.Contains(t.Start))
                    queue.Enqueue(t);
            }

            while (queue.Count > 0)
            {
                var b = queue.Dequeue();
                if (seen.Contains(b.Start))
                    continue;
                seen.Add(b.Start);

                // 不越过 else 块或超出范围
                if (b == elseBlock || b.Start >= elseBlock.Start || b.Start <= thenBlock.Start)
                    continue;

                if (!b.Hidden)
                    return b;

                // 隐藏块：继续沿其后继传递
                foreach (var next in b.To)
                {
                    if (!seen.Contains(next.Start))
                        queue.Enqueue(next);
                }
            }

            return null;
        }


        /// <summary>
        /// 判断分支入口是否含有真正会执行的语句。嵌套 if 先被物化后，入口块
        /// 往往是“赋值 + IfStatement”，另一侧仍是普通表达式；只检查 Statement
        /// 会漏掉这种合法的单块 if/else。纯 Condition/Goto 仍属于短路求值外壳，
        /// 不能据此构造分支，否则会把逻辑表达式误写成控制语句。
        /// </summary>
        private static bool HasBranchPayload(Block block)
        {
            return block?.Statements.Any(node =>
                node is not ConditionExpression && node is not GotoExpression) == true;
        }
    }
}
