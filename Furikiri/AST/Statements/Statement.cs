using System.Collections.Generic;

namespace Furikiri.AST.Statements
{
    public abstract class Statement : IAstNode
    {
        public abstract AstNodeType Type { get; }
        public abstract IEnumerable<IAstNode> Children { get; }
        public IAstNode Parent { get; set; }
    }
}