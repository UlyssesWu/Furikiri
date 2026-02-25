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
                ConditionBlock.Hidden = true;
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

            HideBlocks();
            return i;
        }
    }
}