using System.Collections.Generic;

namespace Furikiri.AST
{
    public interface IAstNode
    {
        AstNodeType Type { get; }
        List<IAstNode> Children { get; }
    }
}