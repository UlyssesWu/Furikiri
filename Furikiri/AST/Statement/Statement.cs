using System.Collections.Generic;

namespace Furikiri.AST.Statement
{
    public abstract class Statement : IAstNode
    {
        public abstract AstNodeType Type { get; }
        public abstract List<IAstNode> Children { get; }
    }
}