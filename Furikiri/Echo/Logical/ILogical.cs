using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    enum LogicalBlockType
    {
        None,
        BlockList,
        Statement,
        Logical,
    }

    interface ILogical
    {
        Statement ToStatement();
    }

    interface IConditional
    {
        Expression Condition { get; }
    }
}
