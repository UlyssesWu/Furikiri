using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    class GotoExpression : Expression, IJump
    {
        public override AstNodeType Type => AstNodeType.GotoExpression;
        public override IEnumerable<IAstNode> Children => null;

        public int JumpTo { get; set; }

        public GotoExpression()
        {
        }
    }
}