using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.AST
{
    class BlockStatement : IAstNode
    {
        public AstNodeType Type => AstNodeType.BlockStatement;
        public List<IAstNode> Children { get; }
    }
}