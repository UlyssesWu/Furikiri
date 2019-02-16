using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.AST.Statements
{
    class ContinueStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.ContinueStatement;
        public override IEnumerable<IAstNode> Children { get; }
    }
}