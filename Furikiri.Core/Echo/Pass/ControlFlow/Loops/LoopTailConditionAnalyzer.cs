using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// do-while 尾部条件链的只读分析结果。
    /// </summary>
    internal sealed class LoopTailConditionPlan
    {
        public Expression Condition { get; set; }
        public IReadOnlyList<(Block Block, ConditionExpression Condition)> Prefixes { get; set; }
            = Array.Empty<(Block, ConditionExpression)>();
    }

    /// <summary>
    /// 从最终回边闩锁向前收集共享同一失败出口的短路条件。
    /// </summary>
    /// <remarks>
    /// TJS2 会把 <c>A &amp;&amp; B</c> 编译成两个尾部条件块：A 失败直接退出，
    /// A 成功才执行负责回边的 B。自然循环的 Header 不一定是 A 所在块，因此
    /// 不能只检查“Header + 最后块”。本分析只读取 CFG，不修改语句或可见性。
    /// </remarks>
    internal static class LoopTailConditionAnalyzer
    {
        public static bool TryAnalyze(
            Loop loop,
            Block latch,
            Block exit,
            Expression latchCondition,
            out LoopTailConditionPlan plan)
        {
            plan = null;
            if (loop == null || latch == null || exit == null || latchCondition == null)
            {
                return false;
            }

            var prefixes = new List<(Block Block, ConditionExpression Condition)>();
            var combined = latchCondition;
            var current = latch;
            var visited = new HashSet<Block> { latch };

            while (true)
            {
                var candidates = current.From
                    .Where(block => block != null && block != loop.Header &&
                                    loop.Contains(block) && visited.Add(block))
                    .Select(block => (Block: block,
                        Condition: block.Statements.GetCondition()))
                    .Where(item => item.Condition != null && item.Block.To.Count == 2 &&
                                   ((item.Condition.TrueBranch == current.Start &&
                                     item.Condition.FalseBranch == exit.Start) ||
                                    (item.Condition.FalseBranch == current.Start &&
                                     item.Condition.TrueBranch == exit.Start)))
                    .ToList();
                if (candidates.Count != 1)
                {
                    break;
                }

                var prefix = candidates[0];
                var entersNext = prefix.Condition.TrueBranch == current.Start
                    ? prefix.Condition.Condition
                    : prefix.Condition.Condition.Invert();
                combined = entersNext.And(combined);
                prefixes.Add(prefix);
                current = prefix.Block;
            }

            if (prefixes.Count == 0)
            {
                return false;
            }

            prefixes.Reverse();
            plan = new LoopTailConditionPlan
            {
                Condition = combined,
                Prefixes = prefixes
            };
            return true;
        }
    }
}
