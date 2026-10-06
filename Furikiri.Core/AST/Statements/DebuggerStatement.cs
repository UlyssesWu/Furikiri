using System.Collections.Generic;

namespace Furikiri.AST.Statements
{
    class DebuggerStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.DebuggerStatement;
        public override IEnumerable<IAstNode> Children => System.Array.Empty<IAstNode>();
    }
}
