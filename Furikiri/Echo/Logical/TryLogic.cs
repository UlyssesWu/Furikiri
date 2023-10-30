using System;
using System.Collections.Generic;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    internal class TryLogic : ILogical
    {
        public Block EnterTry { get; set; }
        public Block ExitTry { get; set; }

        public List<Block> Body { get; set; }

        public Expression CatchClause { get; set; }

        public List<Block> CatchBody { get; set; }

        public Statement ToStatement()
        {
            throw new NotImplementedException();
        }
    }
}
