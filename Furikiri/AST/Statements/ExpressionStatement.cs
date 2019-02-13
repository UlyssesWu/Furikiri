using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.AST.Expressions;

namespace Furikiri.AST.Statements
{
    class ExpressionStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.ExpressionStatement;
        public override IEnumerable<IAstNode> Children { get; }

        public Expression Expression { get; set; }

        public ExpressionStatement(Expression expr)
        {
            Expression = expr;
        }
    }
}