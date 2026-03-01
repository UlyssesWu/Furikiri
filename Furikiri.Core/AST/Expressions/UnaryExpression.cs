using System.Collections.Generic;
using Furikiri.Echo;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    class UnaryExpression : Expression, IOperation
    {
        public override AstNodeType Type => AstNodeType.UnaryExpression;

        public override IEnumerable<IAstNode> Children
        {
            get { yield return Target; }
        }

        public bool IsSelfAssignment { get; set; } = false;

        /// <summary>
        /// true = 前置运算符（++x / --x），false = 后置运算符（x++ / x--）
        /// 对应 INCPD/DECPD 等指令：当结果寄存器 res != 0（结果被下游表达式使用）时为 true。
        /// </summary>
        public bool IsPrefix { get; set; } = false;

        public UnaryOp Op { get; set; }
        public TjsVarType? ResultType { get; set; }

        public Expression Target { get; set; }

        public UnaryExpression(Expression target, UnaryOp op)
        {
            Target = target;
            Op = op;
            Target.Parent = this;
        }

        public override string ToString()
        {
            return DebugString;
        }

        private string DebugString => $"{Op.ToSymbol()} {Target}";
    }
}