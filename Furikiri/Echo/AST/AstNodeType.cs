using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.AST
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

        BinaryExpresssion,
        LocalExpression,
    }
}
