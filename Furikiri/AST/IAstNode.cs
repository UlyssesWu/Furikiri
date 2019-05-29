using System.Collections.Generic;
using Furikiri.AST.Expressions;

namespace Furikiri.AST
{
    public enum AstNodeType
    {
        BlockStatement,
        GotoStatement,
        IfStatement,
        IfElseStatement,
        ExpressionStatement,
        ThrowExpression,
        WhileStatement,
        DoWhileStatement,
        BreakStatement,
        ContinueStatement,
        ForStatement,
        ConditionCase,
        DefaultCase,
        SwitchStatement,
        CatchClause,
        TryStatement,

        UnaryExpression,
        BinaryExpression,
        LocalExpression,
        ConstantExpression,
        IdentifierExpression,
        InvokeExpression,
        ReturnExpression,
        GotoExpression,
        ConditionExpression,
        DeleteExpression,
        PropertyAccessExpression,
    }

    public interface IAstNode
    {
        AstNodeType Type { get; }
        IEnumerable<IAstNode> Children { get; }

        IAstNode Parent { get; set; }
    }

    /// <summary>
    /// Expression with an instance (%obj)
    /// </summary>
    public interface IInstance
    {
        bool HideInstance { get; } //TODO: use C#8 default implementations  

        Expression Instance { get; set; }
    }
}