using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    class GotoExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.GotoExpression;
        public override IEnumerable<IAstNode> Children { get; }

        public int JumpTo { get; set; }
        public GotoExpression()
        {
        }
    }
}