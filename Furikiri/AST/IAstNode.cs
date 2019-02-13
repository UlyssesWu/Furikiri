using System.Collections.Generic;

namespace Furikiri.AST
{
    public interface IAstNode
    {
        AstNodeType Type { get; }
        IEnumerable<IAstNode> Children { get; }
    }
}