using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Logical
{
    class IfLogic : ILogical
    {
        public Expression Condition { get; set; }
        public List<IAstNode> Statements { get; set; } = new List<IAstNode>();
        public List<IAstNode> Then => Statements;
        public List<IAstNode> Else { get; set; } = new List<IAstNode>();
    }
}