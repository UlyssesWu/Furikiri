using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class IfLogic : ILogical
    {
        public Expression Condition { get; set; }
        public Block ConditionBlock { get; set; }
        public LogicalBlock Then { get; set; } = new LogicalBlock();
        public LogicalBlock Else { get; set; } = new LogicalBlock();
        
        internal void HideBlocks(bool hideConditionBlock = false)
        {
            if (hideConditionBlock)
            {
                ConditionBlock.Hidden = true;
            }
            Then?.HideBlocks();
            Else?.HideBlocks();
        }

        public Statement ToStatement()
        {
            if (Condition == null && ConditionBlock != null)
            {
                Condition = (ConditionExpression) ConditionBlock.Statements.LastOrDefault(stmt =>
                    stmt is ConditionExpression);
            }

            IfStatement i = new IfStatement(Condition, Then.ToStatement(), Else.ToStatement());
            HideBlocks();
            return i;
        }
    }
}