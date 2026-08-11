using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class IfLogic : ILogical, IConditional
    {
        public Expression Condition { get; set; }
        public Block ConditionBlock { get; set; }
        public LogicalBlock Then { get; set; } = new LogicalBlock();
        public LogicalBlock Else { get; set; } = new LogicalBlock();
        public IfLogic ParentIf { get; set; }
        public Block PostDominator { get; set; }

        internal void HideBlocks(bool hideConditionBlock = false)
        {
            if (hideConditionBlock)
            {
                // 合成的多路 else-if 共用同一个条件根块；隐藏内层“条件块”
                // 会连同外层语句及其准备表达式一起丢失。
                if (ConditionBlock != null && ParentIf?.ConditionBlock != ConditionBlock)
                {
                    ConditionBlock.Hidden = true;
                }
            }

            Then?.HideBlocks();
            Else?.HideBlocks();
        }

        public void Invert()
        {
            Condition = Condition.Invert();
            (Then, Else) = (Else, Then);
        }

        public IfLogic Simplify()
        {
            if (Then.Statement == null && Else.IsBreak)
            {
                Invert();
                Else.Statement = null;
            }

            return this;
        }

        public Statement ToStatement()
        {
            if (Condition == null && ConditionBlock != null)
            {
                Condition = (ConditionExpression) ConditionBlock.Statements.LastOrDefault(stmt =>
                    stmt is ConditionExpression);
            }

            IfStatement i = new IfStatement(Condition, Then.ToStatement(), Else.ToStatement());
            if (ParentIf != null && ParentIf.PostDominator == PostDominator)
            {
                i.IsElseIf = true;
            }

            // Nested if-condition blocks may contain preparation expressions before the
            // condition itself (e.g. temp assignments). If the block is folded into an
            // else-if chain, keep those expressions immediately before the nested if.
            if (ParentIf != null && ConditionBlock != null && Condition != null)
            {
                var conditionIndex = ConditionBlock.Statements.FindIndex(
                    stmt => ReferenceEquals(stmt, Condition));
                var prefix = conditionIndex > 0
                    ? ConditionBlock.Statements.Take(conditionIndex).ToList()
                    : new System.Collections.Generic.List<IAstNode>();
                if (prefix.Count > 0)
                {
                    var block = new BlockStatement();
                    foreach (var node in prefix)
                    {
                        if (node is Statement st)
                        {
                            block.Statements.Add(st);
                        }
                        else if (node is Expression exp)
                        {
                            block.Statements.Add(new ExpressionStatement(exp));
                        }
                    }

                    block.Statements.Add(i);
                    HideBlocks();
                    return block;
                }
            }

            HideBlocks();
            return i;
        }
    }
}
