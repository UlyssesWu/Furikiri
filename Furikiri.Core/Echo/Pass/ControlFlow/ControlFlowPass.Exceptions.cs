using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.Echo.Logical;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        private void BuildTry(System.Func<Block, bool> entryFilter = null)
        {
            List<Block> SelectBlocksInRange(int startInclusive, int endExclusive)
            {
                return _context.Blocks
                    .Where(b => b.Start >= startInclusive && b.Start < endExclusive)
                    .OrderBy(b => b.Start)
                    .ToList();
            }

            List<Block> SelectReachableBlocks(Block entry, Block exit)
            {
                var selected = new HashSet<Block>();
                var pending = new Stack<Block>();
                pending.Push(entry);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (current == null || current == exit ||
                        current.Start < entry.Start ||
                        (exit.Start > entry.Start && current.Start >= exit.Start) ||
                        !selected.Add(current))
                    {
                        continue;
                    }

                    foreach (var next in current.To)
                    {
                        pending.Push(next);
                    }
                }

                return selected.OrderBy(candidate => candidate.Start).ToList();
            }

            Block GetJumpTarget(Block b)
            {
                var last = b.Instructions.LastOrDefault();
                if (last == null || last.OpCode != OpCode.JMP)
                {
                    return null;
                }

                var jumpData = last.Data as JumpData;
                if (jumpData == null)
                {
                    return null;
                }

                return _context.BlockTable.TryGetValue(jumpData.Goto.Line, out var target) ? target : null;
            }

            Block FindTryEndByExtryJump(Block enterTry, Block catchOrExitTry)
            {
                return _context.Blocks
                    .Where(b => b.Start > enterTry.Start && b.Start < catchOrExitTry.Start)
                    .Where(b => b.Instructions.Any(i => i.OpCode == OpCode.EXTRY))
                    .Select(GetJumpTarget)
                    // A try at the end of a loop jumps back to its header on
                    // success. Its catch still owns the following forward blocks.
                    .Where(target => target != null &&
                        (target.Start > catchOrExitTry.Start || target.Start <= enterTry.Start))
                    .OrderBy(target => target.Start <= enterTry.Start ? 1 : 0)
                    .ThenBy(target => target.Start)
                    .FirstOrDefault();
            }

            Block FindTryEnd(Block startTry, Block catchOrExitTry, Block current)
            {
                if (current.Start <= startTry.Start)
                {
                    return null;
                }

                if (current.Instructions.Any(i => i.OpCode == OpCode.EXTRY))
                {
                    var lastIns = current.Instructions.LastOrDefault();
                    if (lastIns is { OpCode: OpCode.JMP })
                    {
                        var target = current.To.First();
                        if (target.Start >= catchOrExitTry.Start)
                        {
                            return target;
                        }
                    }
                }

                foreach (var next in current.From)
                {
                    var b = FindTryEnd(startTry, catchOrExitTry, next);
                    if (b != null)
                    {
                        return b;
                    }
                }

                return null;
            }

            var entryBlocks = _context.Blocks
                .Where(b => b.Instructions.LastOrDefault()?.OpCode == OpCode.ENTRY)
                .Where(b => entryFilter == null || entryFilter(b))
                .OrderByDescending(b => b.Start)
                .ToList();

            foreach (var block in entryBlocks)
            {
                TryLogic t = new TryLogic();
                t.EnterTry = block;
                t.CatchClause = (Expression)block.Statements.LastOrDefault(stmt => stmt is CatchExpression);
                var catchOrExitTry = _context.BlockTable[((JumpData)block.Instructions.Last().Data).Goto.Line];
                var tryEnd = FindTryEndByExtryJump(block, catchOrExitTry) ?? FindTryEnd(block, catchOrExitTry, catchOrExitTry);
                t.ExitTry = tryEnd ?? catchOrExitTry;
                var containingLoop = _context.LoopSet
                    .Where(loop => loop.Contains(block))
                    .OrderBy(loop => loop.Blocks.Count)
                    .FirstOrDefault();
                var containingLoopExit = containingLoop?.Break ??
                                         (containingLoop?.LoopLogic as DoWhileLogic)?.Break;
                t.ExitTryBreaksLoop = containingLoopExit != null &&
                                      t.ExitTry == containingLoopExit;
                t.Body = SelectBlocksInRange(block.Start + 1, catchOrExitTry.Start);
                if (tryEnd != null && tryEnd != catchOrExitTry) // has catch
                {
                    // catch 后面常紧接外围 else-if 的下一项，但 catch 自己会直接
                    // 跳到 try 的公共出口。按地址范围收集会把这些不可达分支全部
                    // 吞进 catch；应只沿 catch 入口实际可达的 CFG 边收集。
                    t.CatchBody = SelectReachableBlocks(catchOrExitTry, tryEnd);
                }

                // 在 try/catch 体内结构化 if-else（需在 ToStatement 之前，
                // 因为 ToStatement 会隐藏体内块并过滤掉未结构化的 ConditionExpression）
                StructureLoopBodyIfElse(t.Body);
                if (t.CatchBody != null)
                    StructureLoopBodyIfElse(t.CatchBody);

                var tryStatement = t.ToStatement();
                if (t.CatchClause != null && block.Statements.Contains(t.CatchClause))
                {
                    block.Statements.Replace(t.CatchClause, tryStatement);
                }
                else
                {
                    block.Statements.Insert(0, tryStatement);
                }
            }
        }
    }
}
