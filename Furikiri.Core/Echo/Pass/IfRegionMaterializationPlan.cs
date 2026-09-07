using System;
using System.Collections.Generic;
using System.Linq;

namespace Furikiri.Echo.Pass
{
    internal enum IfRegionMaterializationKind
    {
        None,
        ThenSingleArm,
        ElseSingleArm,
        MultiBlock
    }

    /// <summary>
    /// 多块条件区域在 AST 物化前确定的所有权计划。
    /// </summary>
    /// <remarks>
    /// 计划冻结两臂当前拥有的基本块。递归物化子条件只允许隐藏这些块，不能在
    /// AST 已改变后重新扫描整张图并顺便取得新的基本块所有权。
    /// </remarks>
    internal sealed class IfRegionMaterializationPlan
    {
        public IfRegionMaterializationKind Kind { get; }
        public IReadOnlyList<Block> ThenBlocks { get; }
        public IReadOnlyList<Block> ElseBlocks { get; }
        public IReadOnlyList<Block> NestedCandidates { get; }

        public IfRegionMaterializationPlan(
            IfRegionMaterializationKind kind,
            IReadOnlyList<Block> thenBlocks,
            IReadOnlyList<Block> elseBlocks)
        {
            Kind = kind;
            ThenBlocks = thenBlocks ?? Array.Empty<Block>();
            ElseBlocks = elseBlocks ?? Array.Empty<Block>();
            NestedCandidates = ThenBlocks.Concat(ElseBlocks)
                .Distinct()
                .OrderBy(block => block.Start)
                .ToArray();
        }

        public IfRegionMaterializationLayout ResolveVisibleLayout()
        {
            var thenBlocks = ThenBlocks.Where(block => !block.Hidden).ToArray();
            var elseBlocks = ElseBlocks.Where(block => !block.Hidden).ToArray();
            return new IfRegionMaterializationLayout(
                thenBlocks.Length == 0 && elseBlocks.Length > 0,
                thenBlocks.Length == 0 ? elseBlocks : thenBlocks,
                thenBlocks.Length == 0 || elseBlocks.Length == 0
                    ? Array.Empty<Block>()
                    : elseBlocks);
        }
    }

    internal sealed class IfRegionMaterializationLayout
    {
        public bool InvertCondition { get; }
        public IReadOnlyList<Block> ThenBlocks { get; }
        public IReadOnlyList<Block> ElseBlocks { get; }
        public bool HasContent => ThenBlocks.Count > 0 || ElseBlocks.Count > 0;

        public IfRegionMaterializationLayout(
            bool invertCondition,
            IReadOnlyList<Block> thenBlocks,
            IReadOnlyList<Block> elseBlocks)
        {
            InvertCondition = invertCondition;
            ThenBlocks = thenBlocks ?? Array.Empty<Block>();
            ElseBlocks = elseBlocks ?? Array.Empty<Block>();
        }
    }

    /// <summary>
    /// 只根据已证明的两臂所有权选择物化布局，不生成 AST、不修改基本块。
    /// </summary>
    internal static class IfRegionMaterializationAnalyzer
    {
        public static IfRegionMaterializationPlan Analyze(
            Block thenEntry,
            Block elseEntry,
            Block continuation,
            IReadOnlyList<Block> thenBlocks,
            IReadOnlyList<Block> elseBlocks)
        {
            var frozenThen = Freeze(thenBlocks);
            var frozenElse = Freeze(elseBlocks);

            if (frozenThen.Length == 1 && frozenElse.Length == 0 &&
                elseEntry == continuation)
            {
                return new IfRegionMaterializationPlan(
                    IfRegionMaterializationKind.ThenSingleArm,
                    frozenThen, frozenElse);
            }

            if (frozenElse.Length == 1 && frozenThen.Length == 0 &&
                thenEntry == continuation)
            {
                return new IfRegionMaterializationPlan(
                    IfRegionMaterializationKind.ElseSingleArm,
                    frozenThen, frozenElse);
            }

            // 两边都至多一个块时，break/continue、else-if 和短路链仍交给更细的
            // 单块路径。这里只取得至少一臂确实为多块区域的候选。
            var kind = frozenThen.Length <= 1 && frozenElse.Length <= 1
                ? IfRegionMaterializationKind.None
                : IfRegionMaterializationKind.MultiBlock;
            return new IfRegionMaterializationPlan(kind, frozenThen, frozenElse);
        }

        private static Block[] Freeze(IEnumerable<Block> blocks)
        {
            return blocks?
                .Where(block => block != null)
                .Distinct()
                .OrderBy(block => block.Start)
                .ToArray() ?? Array.Empty<Block>();
        }
    }
}
