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
        /// 早退守卫可能由三段以上短路条件组成，例如
        /// `!focusable || (!numLinks &amp;&amp; !hasClick())`。若先从尾部物化，外层
        /// 条件会把正常继续区域误当成自己的 then，并隐藏后面的完整分派。先从
        /// 最早入口恢复所有“只经条件/跳板可达纯 return”的链；单条件会由通用
        /// if 恢复处理，因此这里不会抢走普通分支。
        /// </summary>
        private void StructureTerminalConditionChains(TerminalConditionChainStage stage)
        {
            foreach (var candidate in TerminalConditionChainPlanner.Create(
                         _context, stage))
            {
                var root = candidate.Root;
                if (root.Hidden)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition == null)
                {
                    continue;
                }

                IfLogic logic = null;
                var structured = stage switch
                {
                    TerminalConditionChainStage.PureCondition =>
                        TryStructureConditionChain(root, condition, out logic),
                    TerminalConditionChainStage.EntryPayload =>
                        TryStructurePureDecisionDagToReturnGuard(root, out logic) ||
                        TryStructureConditionChain(root, condition, out logic) ||
                        (candidate.HasPrefixSideEffect &&
                         TryStructurePureDecisionDagToPayload(root, out logic)),
                    _ => false
                };
                if (structured)
                {
                    root.Statements.Replace(condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 外围 switch/default 或长条件链会一次隐藏整个支配区域。
        /// 对 `if (A &amp;&amp; B) return value;` 这种两段条件共享失败出口、
        /// 命中后直接终止的标准形状，必须在外围区域收集前先物化。
        /// 否则中间条件会成为空 if，真实 return 块虽被标记隐藏，
        /// 却没有任何 AST 节点继续引用它。
        /// </summary>
        private void StructureTerminalTwoConditionGuards()
        {
            foreach (var root in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         block.Statements.IsCondition() &&
                                         block.To.Count == 2 &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)))
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    TryStructureTwoConditionTerminalGuard(root, out var logic))
                {
                    root.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        private bool TryStructureTwoConditionTerminalGuard(
            Block root, out IfLogic logic)
        {
            logic = null;
            var rootCondition = root?.Statements?.GetCondition();
            if (rootCondition == null || !root.Statements.IsCondition() ||
                root.To.Count != 2 || _context?.BlockTable == null)
            {
                return false;
            }

            foreach (var second in root.To.Where(block =>
                         !block.Hidden && block.Statements.IsCondition()).ToList())
            {
                var continuation = root.To.First(block => block != second);
                var secondCondition = second.Statements.GetCondition();
                if (secondCondition == null || second.To.Count != 2 ||
                    !second.To.Contains(continuation))
                {
                    continue;
                }

                var body = second.To.First(block => block != continuation);
                // 多段 `A && B || C && D` 会让多个条件共同进入同一个 return
                // 主体；那属于一条完整短路链，不能只提前物化其中两段。
                if (body.From.Count != 1 || body.From[0] != second ||
                    !body.Statements.Any(statement =>
                        statement is ReturnExpression or ThrowExpression))
                {
                    continue;
                }

                var entersSecond = rootCondition.TrueBranch == second.Start
                    ? rootCondition.Condition
                    : rootCondition.Condition.Invert();
                var entersBody = secondCondition.TrueBranch == body.Start
                    ? secondCondition.Condition
                    : secondCondition.Condition.Invert();
                logic = new IfLogic
                {
                    ConditionBlock = root,
                    Condition = entersSecond.And(entersBody),
                    PostDominator = continuation,
                    Then =
                    {
                        Type = LogicalBlockType.BlockList,
                        Blocks = new List<Block> { body }
                    },
                    Else = { Type = LogicalBlockType.None }
                };
                second.Hidden = true;
                return true;
            }

            return false;
        }

        private void StructureTerminalGuards(TerminalGuardStage stage)
        {
            foreach (var plan in TerminalGuardPlanner.Create(_context, stage))
            {
                var logic = TerminalGuardMaterializer.Materialize(plan);
                plan.Root.Statements.Replace(
                    plan.OriginalCondition, logic.Simplify().ToStatement());
            }
        }

        /// <summary>
        /// 恢复嵌套正文中的 `value = ...; if (test) return ...;`。从后向前
        /// 处理可保证外层分支收集到已经完成的早退守卫，而不是裸条件快照。
        /// </summary>
        private void StructureNestedPayloadReturnGuards()
        {
            foreach (var block in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             candidate.From.Count > 0 &&
                                             !candidate.Statements.IsCondition() &&
                                             candidate.Statements.GetCondition() != null &&
                                             candidate.To.Count == 2 &&
                                             candidate.To.Any(TerminalConditionChainPlanner
                                                 .IsSimpleTerminalReturnBlock) &&
                                             candidate.Statements
                                                 .Take(candidate.Statements.Count - 1)
                                                 .Any(statement =>
                                                     statement is BinaryExpression or UnaryExpression) &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                var condition = block.Statements.GetCondition();
                var allDirectPureReturnArms = block.To
                    .Where(target => target.Statements.Count > 0 &&
                                     target.Statements.Any(statement =>
                                         statement is ReturnExpression) &&
                                     target.Statements.All(statement =>
                                         statement is ReturnExpression or GotoExpression))
                    .ToList();
                var assignmentContinuesPredecessorGuard =
                    block.Statements.Take(block.Statements.Count - 1)
                        .OfType<BinaryExpression>()
                        .Any(assignment => assignment.Op == BinaryOp.Assign &&
                                           ReferencesAssignmentTarget(
                                               condition, assignment.Left)) &&
                    block.From.Any(predecessor =>
                        predecessor.Statements.GetCondition() != null &&
                        predecessor.To.Any(allDirectPureReturnArms.Contains));
                if (assignmentContinuesPredecessorGuard)
                {
                    continue;
                }

                if (TerminalGuardPlanner.TryAnalyze(block, out var terminalPlan) &&
                    terminalPlan.Kind == TerminalGuardKind.Return)
                {
                    var guard = TerminalGuardMaterializer.Materialize(terminalPlan);
                    block.Statements.Replace(
                        condition, guard.Simplify().ToStatement());
                    continue;
                }

                if (condition != null &&
                    TryStructurePureDecisionDagToReturnGuard(block, out var logic))
                {
                    block.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 先物化两臂都直接结束函数的叶子判断。外围类型/对象守卫若先收集区域，
        /// 这种内层 else-if 会留下裸比较，并把其中一个 return 提升成无条件返回。
        /// </summary>
        private void StructureTerminalReturnLeaves(IEnumerable<Block> candidates)
        {
            if (candidates == null || _context?.BlockTable == null)
            {
                return;
            }

            foreach (var candidate in candidates
                         .Where(block => !block.Hidden && block.To.Count == 2 &&
                                         block.Statements.GetCondition() != null)
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                bool IsPureReturn(Block target) =>
                    target.Statements.Count > 0 &&
                    target.Statements.All(statement =>
                        statement is ReturnExpression or GotoExpression) &&
                    target.Statements.Any(statement => statement is ReturnExpression);

                if (!candidate.To.All(IsPureReturn))
                {
                    continue;
                }

                if (EqualityDispatchAnalyzer.IsPartOfDispatchChain(candidate))
                {
                    continue;
                }

                var condition = candidate.Statements.GetCondition();

                // 两臂虽然都是 return，但其中一臂有时也是外围条件的公共出口。
                // 例如 `if (p == 0) return; return value;` 的最终 return 会同时被
                // 外围类型判断引用。这里只内联当前条件独占的提前返回，公共出口
                // 留在原控制流中，避免被错误复制进 if 后变成无条件 return。
                // 已合并成 &&/|| 的条件通常仍属于更大的判定 DAG，交给后续
                // 路径谓词合并；过早物化会把整体真假方向反转两次。
                var isMergedLogicalCondition = condition.Condition is BinaryExpression
                {
                    Op: BinaryOp.LogicAnd or BinaryOp.LogicOr
                };
                if (candidate.To.Any(target =>
                        EqualityDispatchAnalyzer.IsSharedCaseBody(candidate, target)))
                {
                    // 当前比较是相等分派的最后一个标签，且其中一臂还是前一
                    // 标签的公共正文。保留完整比较链，稍后由 equality 区域一次
                    // 取得所有标签与共享正文的所有权。
                    continue;
                }

                if (!isMergedLogicalCondition &&
                    TerminalGuardPlanner.TryAnalyze(candidate, out var terminalPlan) &&
                    terminalPlan.Kind == TerminalGuardKind.Return &&
                    terminalPlan.IsPureTerminal)
                {
                    var guard = TerminalGuardMaterializer.Materialize(terminalPlan);
                    candidate.Statements.Replace(
                        condition, guard.Simplify().ToStatement());
                    continue;
                }

                if (candidate.To.Any(target => target.From.Any(predecessor =>
                        predecessor != candidate &&
                        predecessor.Statements.GetCondition() != null)))
                {
                    continue;
                }

                if (StructureIfElse(candidate, out var logic))
                {
                    candidate.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }
    }
}
