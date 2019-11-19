using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    public interface IJump
    {
        int JumpTo { get; set; }
    }

    public interface IOperation
    {
        bool IsSelfAssignment { get; set; }
    }

    public abstract class Expression : IAstNode
    {
        public abstract AstNodeType Type { get; }
        public abstract IEnumerable<IAstNode> Children { get; }
        public IAstNode Parent { get; set; }
    }
}