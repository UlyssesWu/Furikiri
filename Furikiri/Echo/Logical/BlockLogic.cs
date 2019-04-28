using System.Collections.Generic;
using Furikiri.AST;

namespace Furikiri.Echo.Logical
{
    class BlockLogic : ILogical
    {
        public List<IAstNode> Statements { get; set; } = new List<IAstNode>();
    }
}