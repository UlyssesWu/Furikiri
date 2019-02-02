using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.AST
{
    public abstract class Statement : IAstNode
    {
        public abstract AstNodeType Type { get; }
        public abstract List<IAstNode> Children { get; }
    }
}