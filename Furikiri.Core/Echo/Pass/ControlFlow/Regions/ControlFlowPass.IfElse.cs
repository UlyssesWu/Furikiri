using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        /// <summary>
        /// 将延续块的 "if (negated_cond) goto after_scope" 模式转换为正向条件的 then-only if。
        /// 编译器将 if (COND) { body } 编译为 "if (!COND) skip; body"，
        /// 导致 TrueBranch 指向 scope 外部（elseBlock）。
        /// 此方法反转条件，将 FalseBranch（实际 body）作为 then，无 else。
        /// </summary>
        private void StructureContinuationAsInvertedIf(
            Block block, ConditionExpression cond, Block outerElseBlock)
        {
            var invertedCond = (ConditionExpression)cond.Invert();
            var bodyBlock = _context.BlockTable[invertedCond.TrueBranch];

            var innerLogic = new IfLogic
            {
                ConditionBlock = block,
                Condition = invertedCond,
                PostDominator = outerElseBlock,
                Then = { Blocks = new List<Block> { bodyBlock } },
                Else = { Type = LogicalBlockType.None }
            };

            // 递归结构化 body 块
            if (bodyBlock.To.Count == 2 && bodyBlock.Statements.GetCondition() != null)
            {
                var bodyCondition = bodyBlock.Statements.GetCondition();
                if (StructureIfElse(bodyBlock, out IfLogic innerIf))
                {
                    bodyBlock.Statements.Replace(bodyCondition, innerIf.Simplify().ToStatement());
                }
            }

            // 在 body 分支内查找延续块
            if (bodyBlock.To.Any(b => b.Hidden))
            {
                var bodyVisited = new HashSet<int> { bodyBlock.Start };
                var bodyCurrentBlock = bodyBlock;
                while (true)
                {
                    var bodyCont = FindVisibleContinuation(
                        bodyCurrentBlock, bodyVisited, bodyBlock, outerElseBlock);
                    if (bodyCont == null) break;
                    innerLogic.Then.Blocks.Add(bodyCont);
                    bodyVisited.Add(bodyCont.Start);

                    if (bodyCont.To.Count == 2 && bodyCont.Statements.GetCondition() != null)
                    {
                        var bodyContinuationCondition = bodyCont.Statements.GetCondition();
                        if (StructureIfElse(bodyCont, out IfLogic nestedCont2))
                        {
                            bodyCont.Statements.Replace(bodyContinuationCondition,
                                nestedCont2.Simplify().ToStatement());
                        }
                    }

                    bodyCurrentBlock = bodyCont;
                }
            }

            block.Statements.Replace(cond, innerLogic.Simplify().ToStatement());
        }

        internal bool StructureIfElse(Block block, out IfLogic outIf)
        {
            outIf = null;
            // Earlier chains can consume entries still present in a caller's
            // candidate snapshot. Structuring them again steals shared bodies.
            if (block == null || block.Hidden || !_structuringBlocks.Add(block.Id))
            {
                return false;
            }

            using var mutation = new ControlFlowMutationScope(_context.Blocks);
            try
            {
                var structured = StructureIfElseCore(block, out outIf, true);
                if (structured)
                {
                    mutation.Commit();
                }
                return structured;
            }
            finally
            {
                _structuringBlocks.Remove(block.Id);
            }
        }

        /// <summary>
        /// 在父区域冻结的所有权集合内尝试物化子条件。
        /// </summary>
        private bool TryStructureIfElseWithinOwnership(
            Block block, Block boundary, ISet<Block> ownedBlocks,
            out IfLogic outIf, out bool ownershipViolation)
        {
            outIf = null;
            ownershipViolation = false;

            bool Attempt(Block forcedBoundary, bool allowConditionChain,
                out IfLogic logic, out bool crossedBoundary)
            {
                logic = null;
                crossedBoundary = false;
                if (block == null || block.Hidden || !_structuringBlocks.Add(block.Id))
                {
                    return false;
                }

                using var mutation = new ControlFlowMutationScope(_context.Blocks);
                try
                {
                    var structured = StructureIfElseCore(
                        block, out logic, allowConditionChain, forcedBoundary);
                    if (!structured)
                    {
                        return false;
                    }

                    crossedBoundary = mutation.HasChangesOutside(ownedBlocks);
                    if (crossedBoundary)
                    {
                        logic = null;
                        return false;
                    }

                    mutation.Commit();
                    return true;
                }
                finally
                {
                    _structuringBlocks.Remove(block.Id);
                }
            }

            var loop = _context.LoopSet
                .Where(candidate => candidate.Contains(block))
                .OrderBy(candidate => candidate.Blocks.Count)
                .FirstOrDefault();
            var boundaryReachCount = boundary == null
                ? 0
                : block.To.Count(target =>
                    _graph.CanReachWithoutEntering(
                        target, boundary, loop?.Header));
            var hasMixedBoundaryExit = block.To.Count == 2 &&
                                       boundaryReachCount == 1;

            // 一臂正常落入父公共尾部、另一臂结束本轮时，普通后支配点会跨到
            // 下一轮。此形态必须先使用父边界；若不能在所有权内完成，父计划
            // 整体回滚，不能退回会吞掉边界的普通结构化。
            if (hasMixedBoundaryExit)
            {
                if (Attempt(boundary, false, out outIf, out _))
                {
                    return true;
                }
                ownershipViolation = true;
                return false;
            }

            if (Attempt(null, true, out outIf, out var firstViolation))
            {
                return true;
            }

            ownershipViolation = firstViolation;
            return false;
        }

        /// <summary>
        /// 将当前条件独占的空跳板链归一化为真实公共继续点。
        /// </summary>
        private static Block TryGetEmptyBranchPassthrough(
            Block owner, Block entry, Block continuation,
            out List<Block> passthrough)
        {
            passthrough = new List<Block>();
            if (owner == null || entry == null || continuation == null ||
                entry == continuation)
            {
                return entry;
            }

            var current = entry;
            var predecessor = owner;
            var visited = new HashSet<Block>();
            while (current != continuation && visited.Add(current))
            {
                if (current.Hidden || HasBranchPayload(current) ||
                    current.To.Count != 1 ||
                    current.From.Any(source => source != predecessor))
                {
                    passthrough.Clear();
                    return entry;
                }

                passthrough.Add(current);
                predecessor = current;
                current = current.To[0];
            }

            if (current != continuation)
            {
                passthrough.Clear();
                return entry;
            }

            return continuation;
        }
    }
}
