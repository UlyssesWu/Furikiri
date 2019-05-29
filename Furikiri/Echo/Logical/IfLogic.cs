using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class IfLogic : ILogical
    {
        public Expression Condition { get; set; }
        public List<IAstNode> Statements { get; set; } = new List<IAstNode>();
        public List<Block> Then { get; set; }
        public List<Block> Else { get; set; }
        public Statement ToStatement()
        {
            IfStatement i = new IfStatement(Condition, new BlockStatement(Then, true), new BlockStatement(Else, true));
            return i;
        }
    }
}