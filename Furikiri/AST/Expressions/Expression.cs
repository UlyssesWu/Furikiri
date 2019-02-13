using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    public abstract class Expression : IAstNode
    {
        public abstract AstNodeType Type { get; }
        public abstract IEnumerable<IAstNode> Children { get; }

        protected List<Instruction> Instructions { get; } = new List<Instruction>();
    }
}