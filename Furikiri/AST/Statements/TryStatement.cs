using System.Collections.Generic;

namespace Furikiri.AST.Statements
{
    internal class TryStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.TryStatement;
        public override IEnumerable<IAstNode> Children { get; }

        public BlockStatement Try { get; set; }

        public ExpressionStatement Catch { get; set; }

        public BlockStatement Finally { get; set; }
    }
}
