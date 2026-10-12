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
        /// 将入口处只由条件块组成、最终选择“直接 return”或“进入函数正文”的
        /// 判定 DAG 恢复成提前返回守卫。正文入口可能自身带赋值和后续条件，
        /// 因而不能继续沿整张函数 CFG 搜索终点。
        /// </summary>
        private bool TryStructurePureDecisionDagToReturnGuard(
            Block root, out IfLogic logic)
        {
            logic = null;
            if (!DecisionDagAnalyzer.TryAnalyzeReturnGuard(
                    _context, _graph, root, out var plan))
            {
                return false;
            }

            logic = DecisionDagMaterializer.Materialize(plan);
            return true;
        }


        /// <summary>
        /// 带前置调用的条件入口后面可能是一张共享节点的纯布尔 DAG，最终只在
        /// “执行一个载荷块”和“直接结束”之间选择。逐层 if 恢复会在共享节点处
        /// 错误嵌套；直接计算到载荷块的路径谓词可完整保留 OR/AND 关系。
        /// </summary>
        private bool TryStructurePureDecisionDagToPayload(Block root, out IfLogic logic)
        {
            logic = null;
            if (!DecisionDagAnalyzer.TryAnalyzePayload(
                    _context, _graph, root, out var plan))
            {
                return false;
            }

            logic = DecisionDagMaterializer.Materialize(plan);
            return true;
        }

        private void StructureTwoPayloadDecisionDags()
        {
            foreach (var root in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         block.Statements.GetCondition() != null &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)))
                         .OrderBy(block => block.Start)
                         .ToList())
            {
                var condition = root.Statements.GetCondition();
                if (condition == null ||
                    !DecisionDagAnalyzer.TryAnalyzeTwoPayloadBranches(
                        _context, _graph, root, out var plan))
                {
                    continue;
                }

                var logic = DecisionDagMaterializer.Materialize(plan);
                root.Statements.Replace(
                    condition, logic.Simplify().ToStatement());
            }
        }

        private void StructureSideEffectDecisionDags(
            bool pureDecisionDagOnly = false)
        {
            foreach (var root in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)) &&
                                         !block.Statements.IsCondition() &&
                                         block.Statements.GetCondition() != null &&
                                         (!pureDecisionDagOnly ||
                                          ExpressionEffectAnalysis.HasObservableEffect(
                                              block.Statements.GetCondition().Condition)) &&
                                             block.Statements
                                                 .Take(block.Statements.Count - 1)
                                                 .OfType<Expression>()
                                             .Any(ExpressionEffectAnalysis
                                                 .HasObservableEffectBeyondLocalInitialization))
                         // 外层区域会复制分支入口的当前语句，因此先写回内层。
                         .OrderByDescending(block => block.Start)
                .ToList())
            {
                var condition = root.Statements.GetCondition();
                // 内层判断已经物化后，入口的两条边就是普通正文/继续点。
                // 此时使用完整支配区域收集，才能把正文后半段也保留在外层 if；
                // 仍含条件 DAG 时再按路径谓词恢复。
                var hasImmediateDecisionTarget = root.To.Any(target =>
                    target.Statements.GetCondition() != null);
                if ((!pureDecisionDagOnly && !hasImmediateDecisionTarget &&
                     StructureIfElse(root, out var logic)) ||
                    TryStructurePureDecisionDagToPayload(root, out logic))
                {
                    root.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        private bool IsPureConditionRegion(Block root)
        {
            var pending = new Stack<Block>();
            var visited = new HashSet<Block>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                var condition = current.Statements.GetCondition();
                if (condition == null)
                {
                    continue;
                }

                // 调用、赋值和自增等求值顺序本身就是语义的一部分。此类短路链
                // 必须留给常规恢复，不能仅因入口是公共尾部而提前物化。
                if (!current.Statements.IsCondition() ||
                    ExpressionEffectAnalysis.HasObservableEffect(condition.Condition))
                {
                    return false;
                }

                foreach (var target in current.To.Where(
                             target => target.Statements.GetCondition() != null))
                {
                    pending.Push(target);
                }
            }

            return true;
        }
    }
}
