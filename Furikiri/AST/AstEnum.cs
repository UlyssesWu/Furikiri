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
    }

}