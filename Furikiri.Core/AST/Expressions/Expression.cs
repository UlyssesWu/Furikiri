using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    public interface IJump
    {
        int JumpTo { get; set; }
    }

    public interface IOperation
    {
        bool IsSelfAssignment { get; set; }
    }

    public abstract class Expression : IAstNode
    {
        public abstract AstNodeType Type { get; }
        public abstract IEnumerable<IAstNode> Children { get; }
        public IAstNode Parent { get; set; }

        /// <summary>
        /// 表达式结果被保存到 VM 临时寄存器时记录其槽位。该信息仅供控制流
        /// 恢复判断“同一次求值被多次比较”，不会影响源码输出。
        /// </summary>
        internal short? CachedTemporarySlot { get; set; }

        /// <summary>
        /// 产生缓存值的 VM 指令位置。同一个临时槽会被多次复用，仅凭槽号
        /// 不能区分一次 switch 求值与源码中两次独立的 typeof/调用。
        /// </summary>
        internal int? CachedEvaluationId { get; set; }

        // use-def 已确认值被丢弃，但产生值的 getter 等求值仍必须独立执行。
        internal bool RequiresStandaloneEvaluation { get; set; }
    }
}
