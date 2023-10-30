using System;
using System.Collections.Generic;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    internal class TryLogic : ILogical
    {
        public List<Block> Body { get; set; }

        public Expression Catch { get; set; }

        public List<Block> Finally { get; set; }

        public Statement ToStatement()
        {
            throw new NotImplementedException();
        }
    }
}
