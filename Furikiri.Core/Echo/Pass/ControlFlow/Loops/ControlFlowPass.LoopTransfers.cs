using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        private void StructureLoopTransfers(Loop loop, IEnumerable<Block> body, Block continueBlock, Block breakBlock)
        {
            var bodyBlocks = body?.ToList() ?? new List<Block>();
            // 父自然循环包含子循环的全部基本块。若父循环先把子循环的出口跳转
            // 改写成自己的 continue，子循环随后便无法识别该边其实是 break。
            // 转移语句只在当前循环独占的块中恢复；子循环会在自己的轮次处理。
            var childBlocks = loop.Children
                .SelectMany(child => child.Blocks)
                .ToHashSet();
            foreach (var bodyBlock in bodyBlocks)
            {
                if (childBlocks.Contains(bodyBlock))
                {
                    continue;
                }

                StructureBreakContinue(
                    bodyBlock, continueBlock, breakBlock);
            }

        }

        internal void StructureBreakContinue(
            Block b, Block continueBlock, Block breakBlock)
        {
            if (b?.Statements == null)
            {
                return;
            }

            for (var i = 0; i < b.Statements.Count; i++)
            {
                var node = b.Statements[i];
                if (node is GotoExpression g)
                {
                    if (continueBlock != null && g.JumpTo == continueBlock.Start)
                    {
                        b.Statements[i] = new ContinueStatement();
                    }
                    else if (breakBlock != null && g.JumpTo == breakBlock.Start)
                    {
                        b.Statements[i] = new BreakStatement();
                    }
                }
            }
        }

        private static bool IsLoopBreakArm(Block block, Block loopBreak, Loop loop = null)
        {
            if (block == null || loopBreak == null)
            {
                return false;
            }

            if (block == loopBreak || block.Statements.Any(statement => statement is BreakStatement))
            {
                return true;
            }

            if (block.Statements.LastOrDefault() is not GotoExpression jump)
            {
                return false;
            }

            // 一个循环允许多条独立 break 边，它们不一定先汇合到 FindBreak 选中的
            // 公共出口。若纯跳转臂的目标不属于循环，仍应把它恢复成 break；否则
            // 该 goto 在收集阶段会消失，留下空 if/else，并可能把循环变成死循环。
            return jump.JumpTo == loopBreak.Start ||
                   loop != null && loop.Blocks.All(candidate => candidate.Start != jump.JumpTo);
        }

        private static Statement MoveLoopBreakArmToStatement(Block block, Block loopBreak)
        {
            if (block == null || block == loopBreak)
            {
                return new BreakStatement();
            }

            var result = new BlockStatement();
            var moved = block.Statements
                .Where(node => node is not GotoExpression && node is not BreakStatement)
                .ToList();
            foreach (var node in moved)
            {
                block.Statements.Remove(node);
                result.Statements.Add(node is Expression expression
                    ? new ExpressionStatement(expression)
                    : node);
            }
            result.Statements.Add(new BreakStatement());
            return result;
        }

        private bool IsContinueArmWithPayload(Block block, Loop loop)
        {
            if (block == null || loop == null || block.To.Count != 1 ||
                !IsContinueTarget(block.To[0], loop))
            {
                return false;
            }

            var last = block.Statements.LastOrDefault();
            var endsInContinue = last is ContinueStatement ||
                                 last is GotoExpression jump &&
                                 jump.JumpTo == block.To[0].Start;
            return endsInContinue && block.Statements.Any(node =>
                node is not GotoExpression && node is not ContinueStatement);
        }

        private static Statement MoveLoopContinueArmToStatement(Block block)
        {
            var result = new BlockStatement();
            var moved = block.Statements
                .Where(node => node is not GotoExpression && node is not ContinueStatement)
                .ToList();
            foreach (var node in moved)
            {
                block.Statements.Remove(node);
                result.Statements.Add(node is Expression expression
                    ? new ExpressionStatement(expression)
                    : node);
            }

            result.Statements.Add(new ContinueStatement());
            return result;
        }

        /// <summary>
        /// 检查目标块是否是循环的 continue 路径（最终跳转回循环头部）
        /// </summary>
        private bool IsContinueTarget(Block target, Loop loop)
        {
            if (target == null) return false;

            // while/已提升的 for 往往没有独立闩锁块，continue 会直接跳到循环头。
            if (target == loop.Header)
                return true;

            // 直接是循环闩锁块（无语句，仅跳转回循环头部）
            if (target.Statements.Count == 0 && target.To.Count == 1 && target.To[0] == loop.Header)
                return true;

            // 无显式 for 闩锁的 while，源码 continue 常落在一个只含 JMP 的跳板块。
            // GotoExpression 仍保留时也要识别，否则会输出空 if 并把后续语句塞进 else。
            if (target.Statements.Count > 0 &&
                target.Statements.All(statement => statement is GotoExpression) &&
                target.To.Count == 1 && target.To[0] == loop.Header)
                return true;

            // 块只包含 ContinueStatement
            if (target.Statements.Count == 1 && target.Statements[0] is ContinueStatement)
                return true;

            // 空块且后继为 continue 目标（处理连续跳转链）
            if (target.Statements.Count == 0 && target.To.Count == 1)
            {
                var jumpTarget = target.To[0];
                if (jumpTarget != target && IsContinueTarget(jumpTarget, loop))
                    return true;
            }

            return false;
        }
    }
}
