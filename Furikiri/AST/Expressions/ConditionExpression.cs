using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    class ConditionExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.ConditionExpression;
        public override IEnumerable<IAstNode> Children { get; }

        public Expression Condition { get; set; }

        public bool JumpIf { get; set; }

        public int JumpTo { get; set; }

        public ConditionExpression(Expression condition, bool jumpIf = true)
        {
            Condition = condition;
            JumpIf = jumpIf;
        }
    }
}