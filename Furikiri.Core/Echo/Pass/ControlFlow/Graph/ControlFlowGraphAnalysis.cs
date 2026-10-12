using System;
using System.Collections.Generic;
using System.Linq;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 针对原始基本块图的只读查询集合。
    /// </summary>
    /// <remarks>
    /// 结构化阶段会不断替换块中的 AST 并改变 Hidden，但 CFG 的边不应随之变化。
    /// 本类在创建时保存后继快照，使区域识别始终针对同一张图；纯可达性结果可安全
    /// 缓存。涉及 return/throw 等语句事实的查询由调用方传入谓词，迁移期间仍允许
    /// 消费当前 AST 状态，但查询本身不会修改基本块。
    /// </remarks>
    internal sealed class ControlFlowGraphAnalysis
    {
        private readonly Block[] _blocks;
        private readonly Dictionary<Block, Block[]> _successors;
        private readonly Dictionary<(int Start, int Target), bool> _reachability =
            new Dictionary<(int Start, int Target), bool>();
        private readonly Dictionary<(int Start, int Target, int Excluded), bool>
            _reachabilityWithoutEntry =
                new Dictionary<(int Start, int Target, int Excluded), bool>();
        private readonly Dictionary<int, Block> _immediatePostDominators =
            new Dictionary<int, Block>();

        public ControlFlowGraphAnalysis(DecompileContext context)
        {
            _blocks = context?.Blocks?.ToArray() ?? Array.Empty<Block>();
            _successors = _blocks.ToDictionary(
                block => block,
                block => block.To?.ToArray() ?? Array.Empty<Block>());
        }

        public Block FindImmediatePostDominator(Block conditionBlock)
        {
            if (conditionBlock?.PostDominator == null)
            {
                return null;
            }

            if (_immediatePostDominators.TryGetValue(
                    conditionBlock.Id, out var cached))
            {
                return cached;
            }

            var candidates = _blocks
                .Where(block => block != conditionBlock &&
                                conditionBlock.PostDominator[block.Id])
                .ToList();
            var result = candidates.FirstOrDefault(candidate =>
                !candidates.Any(other => other != candidate &&
                    other.PostDominator?[candidate.Id] == true));
            _immediatePostDominators[conditionBlock.Id] = result;
            return result;
        }

        public bool CanReach(Block start, Block target)
        {
            if (start == null || target == null)
            {
                return false;
            }

            var key = (start.Id, target.Id);
            if (_reachability.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var result = Search(start, target, null);
            _reachability[key] = result;
            return result;
        }

        public bool CanReachWithoutEntering(
            Block start, Block target, Block excludedEntry)
        {
            if (start == null || target == null)
            {
                return false;
            }

            if (excludedEntry == null)
            {
                return CanReach(start, target);
            }

            var key = (start.Id, target.Id, excludedEntry.Id);
            if (_reachabilityWithoutEntry.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var result = Search(start, target, excludedEntry);
            _reachabilityWithoutEntry[key] = result;
            return result;
        }

        /// <summary>
        /// 收集从入口可达、但尚未进入公共继续块的基本块。结果来自创建分析对象时
        /// 保存的后继快照，不受随后 AST 物化或 Hidden 标记影响。
        /// </summary>
        public IReadOnlyCollection<Block> CollectReachableBefore(
            Block start, Block stop, Block excludedEntry = null,
            Func<Block, bool> stopsPath = null)
        {
            var result = new HashSet<Block>();
            if (start == null || start == stop)
            {
                return result;
            }

            var pending = new Stack<Block>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == stop || !result.Add(current))
                {
                    continue;
                }

                // return、throw、break、continue 等终止当前源码路径的块仍属于
                // 当前分支，但其分析用后继（统一 ExitBlock 或循环边）不属于分支。
                if (stopsPath?.Invoke(current) == true)
                {
                    continue;
                }

                foreach (var next in GetSuccessors(current))
                {
                    if (next != excludedEntry)
                    {
                        pending.Push(next);
                    }
                }
            }

            return result;
        }

        public bool CanReachMatchingBefore(
            Block start, Block stop, Func<Block, bool> matches)
        {
            if (start == null || matches == null)
            {
                return false;
            }

            var pending = new Stack<Block>();
            var visited = new HashSet<Block>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == stop || !visited.Add(current))
                {
                    continue;
                }

                if (matches(current))
                {
                    return true;
                }

                foreach (var next in GetSuccessors(current))
                {
                    pending.Push(next);
                }
            }

            return false;
        }

        public bool AllPathsTerminateBefore(
            Block start, Block stop, Func<Block, bool> terminates)
        {
            var memo = new Dictionary<Block, bool>();
            var visiting = new HashSet<Block>();
            bool Visit(Block current)
            {
                if (current == null || current == stop)
                {
                    return false;
                }

                if (terminates?.Invoke(current) == true)
                {
                    return true;
                }

                if (memo.TryGetValue(current, out var cached))
                {
                    return cached;
                }

                var successors = GetSuccessors(current);
                if (!visiting.Add(current) || successors.Count == 0)
                {
                    return false;
                }

                var result = successors.All(Visit);
                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Visit(start);
        }

        public bool AllPathsReachOrTerminateAt(
            Block start, Block stop, Func<Block, bool> terminates,
            Func<Block, bool> completesIteration = null)
        {
            var memo = new Dictionary<Block, bool>();
            var visiting = new HashSet<Block>();
            bool Visit(Block current)
            {
                if (current == stop)
                {
                    return true;
                }

                if (current == null)
                {
                    return false;
                }

                if (completesIteration?.Invoke(current) == true ||
                    terminates?.Invoke(current) == true)
                {
                    return true;
                }

                if (memo.TryGetValue(current, out var cached))
                {
                    return cached;
                }

                var successors = GetSuccessors(current);
                if (!visiting.Add(current) || successors.Count == 0)
                {
                    return false;
                }

                var result = successors.All(Visit);
                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Visit(start);
        }

        /// <summary>
        /// 判断区域内的每条路径是否都正常汇合到指定继续块。与允许 return、throw
        /// 提前结束的查询分开，避免把终止型控制区域误认成普通 if 载荷。
        /// </summary>
        public bool AllPathsReach(Block start, Block stop)
        {
            return AllPathsReachOrTerminateAt(start, stop, _ => false);
        }

        private bool Search(Block start, Block target, Block excludedEntry)
        {
            // excludedEntry 用于阻止从循环头重新进入下一轮；若查询本身从该
            // 入口开始，则不能沿循环体继续搜索。反之，当它恰好也是目标时，
            // 到达该块应算成功，而不是把最后一条回边一并过滤掉。
            if (start == excludedEntry && start != target)
            {
                return false;
            }

            var pending = new Queue<Block>();
            var visited = new HashSet<Block>();
            pending.Enqueue(start);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current == target)
                {
                    return true;
                }

                foreach (var next in GetSuccessors(current))
                {
                    if (next != excludedEntry || next == target)
                    {
                        pending.Enqueue(next);
                    }
                }
            }

            return false;
        }

        public IReadOnlyList<Block> GetSuccessors(Block block)
        {
            return block != null && _successors.TryGetValue(block, out var successors)
                ? successors
                : Array.Empty<Block>();
        }
    }
}
