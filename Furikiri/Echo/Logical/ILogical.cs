using System.Collections.Generic;
using Furikiri.AST;

namespace Furikiri.Echo.Logical
{
    interface ILogical
    {
        List<IAstNode> Statements { get; }
    }
}
