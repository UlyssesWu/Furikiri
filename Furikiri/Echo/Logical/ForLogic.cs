using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Logical
{
    class ForLogic : ILogical
    {
        public Expression Initializer { get; set; }
        public Expression Condition { get; set; }
        public Expression Increment { get; set; }
        public List<IAstNode> Statements { get; set; } = new List<IAstNode>();
    }
}