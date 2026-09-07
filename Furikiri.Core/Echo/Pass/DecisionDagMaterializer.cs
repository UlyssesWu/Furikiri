using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 将已经通过只读分析的决策 DAG 计划一次性提交为结构区域。
    /// </summary>
    /// <remarks>
    /// 分析器只描述控制块所有权和终点；本类型是唯一修改 Hidden、删除区域内部
    /// 跳转并创建 IfLogic 的位置。这样不同 DAG 入口不会各自维护略有差异的提交步骤。
    /// </remarks>
    internal static class DecisionDagMaterializer
    {
        public static IfLogic Materialize(DecisionDagReturnPlan plan)
        {
            AdoptControlBlocks(plan);
            return new IfLogic
            {
                ConditionBlock = plan.Root,
                Condition = plan.Predicate,
                PostDominator = plan.BodyTarget,
                Then =
                {
                    Type = LogicalBlockType.BlockList,
                    Blocks = new List<Block> { plan.ReturnTarget }
                },
                Else = { Type = LogicalBlockType.None }
            };
        }

        public static IfLogic Materialize(DecisionDagPlan plan)
        {
            AdoptControlBlocks(plan);
            RemoveRegionExitGoto(plan.Body);
            return new IfLogic
            {
                ConditionBlock = plan.Root,
                Condition = plan.Predicate,
                PostDominator = plan.Continuation,
                Then =
                {
                    Type = LogicalBlockType.BlockList,
                    Blocks = new List<Block> { plan.Body }
                },
                Else = { Type = LogicalBlockType.None }
            };
        }

        public static IfLogic Materialize(DecisionDagBranchPlan plan)
        {
            AdoptControlBlocks(plan);
            RemoveRegionExitGoto(plan.TrueTarget);
            RemoveRegionExitGoto(plan.FalseTarget);
            return new IfLogic
            {
                ConditionBlock = plan.Root,
                Condition = plan.Predicate,
                PostDominator = plan.Continuation,
                Then =
                {
                    Type = LogicalBlockType.BlockList,
                    Blocks = new List<Block> { plan.TrueTarget }
                },
                Else =
                {
                    Type = LogicalBlockType.BlockList,
                    Blocks = new List<Block> { plan.FalseTarget }
                }
            };
        }

        private static void AdoptControlBlocks(DecisionDagRegionPlan plan)
        {
            // 根块保留为区域承载点；其余条件和空跳板只有在计划被采用后才隐藏。
            foreach (var block in plan.OwnedControlBlocks.Where(
                         block => block != plan.Root))
            {
                block.Hidden = true;
            }
        }

        private static void RemoveRegionExitGoto(Block block)
        {
            var jump = block?.Statements?.LastOrDefault(statement =>
                statement is GotoExpression);
            if (jump != null)
            {
                block.Statements.Remove(jump);
            }
        }
    }
}
