using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Logical
{
    class DoWhileLogic : ILogical
    {
        public List<IAstNode> Continue { get; set; }
        public Expression Condition { get; set; }
        public List<IAstNode> Break { get; set; }
        public List<IAstNode> Body { get; set; }
        public List<IAstNode> Statements { get; set; } = new List<IAstNode>();
    }
}
