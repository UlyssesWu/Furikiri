using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.AST.Statements
{
    class BreakStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.BreakStatement;
        public override IEnumerable<IAstNode> Children { get; }
    }
}
