using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    class ThrowExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.ThrowExpression;

        public override IEnumerable<IAstNode> Children
        {
            get { yield return Target; }
        }

        public Expression Target { get; set; }

        public ThrowExpression(Expression target)
        {
            Target = target;
            Target.Parent = this;
        }
    }
}