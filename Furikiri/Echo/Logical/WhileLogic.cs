using System.Collections.Generic;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class WhileLogic : ILogical
    {
        public Block Continue { get; set; }
        public Expression Condition { get; set; }
        public Block Break { get; set; }
        public List<Block> Body { get; set; }
        public bool IsDoWhile { get; set; } = true;
        public Statement ToStatement()
        {
            var body = new BlockStatement(Body, true);
            if (IsDoWhile)
            {
                DoWhileStatement dw = new DoWhileStatement(Condition, body);
                return dw;
            }
            else
            {
                WhileStatement dw = new WhileStatement(Condition, body);
                return dw;
            }
        }
    }
}
