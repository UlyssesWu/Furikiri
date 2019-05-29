using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    interface ILogical
    {
        Statement ToStatement();
    }
}
