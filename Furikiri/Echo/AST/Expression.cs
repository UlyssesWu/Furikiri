using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.AST
{
    public abstract class Expression : IAstNode
    {
        public abstract AstNodeType Type { get; }
        public abstract List<IAstNode> Children { get; }

        protected List<Instruction> Instructions { get; } = new List<Instruction>();
    }
}