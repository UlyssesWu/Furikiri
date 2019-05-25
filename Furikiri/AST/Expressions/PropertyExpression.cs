using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.AST.Expressions
{
    class PropertyExpression : Expression
    {
        public override AstNodeType Type { get; }
        public override IEnumerable<IAstNode> Children { get; }
    }
}
