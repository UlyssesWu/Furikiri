using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.AST.Expressions;

namespace Furikiri.AST.Statements
{
    class IfStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.IfStatement;
        public override IEnumerable<IAstNode> Children { get; }

        public BlockStatement Then { get; set; }
        public Statement Else { get; set; }
        public Expression Condition { get; set; }

        public IfStatement(Expression cond, BlockStatement then, Statement el)
        {
            Condition = cond;
            Then = then;
            Else = el;
        }
    }
}