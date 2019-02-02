using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.AST
{
    public interface IAstNode
    {
        AstNodeType Type { get; }
        List<IAstNode> Children { get; }
    }
}