using System.Collections.Generic;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class DoWhileLogic : ILogical
    {
        public Block Continue { get; set; }
        public Expression Condition { get; set; }
        public Block Break { get; set; }
        public List<Block> Body { get; set; }
        public bool IsWhile { get; set; }

        public DoWhileLogic(bool isWhile = false)
        {
            IsWhile = isWhile;
        }

        public Statement ToStatement()
        {
            var body = new BlockStatement(Body, true);
            if (IsWhile)
            {
                WhileStatement dw = new WhileStatement(Condition, body);
                return dw;
            }
            else
            {
                DoWhileStatement dw = new DoWhileStatement(Condition, body);
                return dw;
            }
        }
    }
}
