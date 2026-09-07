using System.Collections.Generic;
using Furikiri.AST.Expressions;

namespace Furikiri.AST.Statements
{
    class IfStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.IfStatement;
        public override IEnumerable<IAstNode> Children { get; }

        public Statement Then { get; set; }
        public Statement Else { get; set; }
        public Expression Condition { get; set; }
        public bool IsElseIf { get; set; } = false;

        /// <summary>
        /// 该节点来自 CFG 相等分派计划，而不是普通源码级 if 链。
        /// </summary>
        internal bool IsEqualityDispatch { get; set; }

        /// <summary>
        /// 该分派已经由未改写 CFG 中的共享定义身份或编译器收尾跳转确认。
        /// 它只证明源结构是 switch，不扩大未命中区域的所有权。
        /// </summary>
        internal bool HasCompilerSwitchEvidence { get; set; }

        /// <summary>
        /// 编译器收尾指令证明未命中入口是显式 default，而不是 switch 后的
        /// 顺序继续代码。该事实与“节点来自 switch”分开保存，避免扩大区域所有权。
        /// </summary>
        internal bool HasCompilerDefaultEvidence { get; set; }

        /// <summary>
        /// 至少一个 case 正文可沿原 CFG 落入未命中区域；该区域因此属于
        /// 分派正文，而不是所有 case 结束后的普通顺序代码。
        /// </summary>
        internal bool HasEqualityDispatchDefaultFallthrough { get; set; }

        /// <summary>
        /// 当前 case 正文至少有一条路径会自然进入 default 正文。
        /// </summary>
        internal bool FallsThroughToEqualityDispatchDefault { get; set; }

        /// <summary>
        /// 分派公共出口是所属循环的下一轮入口；case 末尾的 continue 因而是
        /// 源码 switch break 经 CFG 结构化后的表示。
        /// </summary>
        internal bool EqualityDispatchEndsAtLoopLatch { get; set; }

        public IfStatement(Expression cond, Statement then, Statement el)
        {
            Condition = cond;
            Then = then;
            Else = el;
        }
    }
}
