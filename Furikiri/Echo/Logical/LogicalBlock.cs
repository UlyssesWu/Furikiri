using System.Collections.Generic;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class LogicalBlock : ILogical
    {
        public LogicalBlockType Type { get; set; } = LogicalBlockType.BlockList;

        public List<Block> Blocks { get; set; } = new List<Block>();
        public Statement Statement { get; set; }
        public ILogical Logic { get; set; }

        public bool IsBreak => Type == LogicalBlockType.Statement && Statement is BreakStatement;

        public Statement ToStatement()
        {
            switch (Type)
            {
                case LogicalBlockType.BlockList:
                    var blockStmt = new BlockStatement(Blocks, true);
                    return blockStmt.Statements.Count > 0 ? blockStmt : null;
                case LogicalBlockType.Statement:
                    return Statement;
                case LogicalBlockType.Logical:
                    return Logic.ToStatement();
                case LogicalBlockType.None:
                default:
                    return null;
            }
        }

        public void HideBlocks()
        {
            switch (Type)
            {
                case LogicalBlockType.BlockList:
                    Blocks?.SafeHide();
                    break;
                case LogicalBlockType.Logical:
                    if (Logic is IfLogic ifLogic)
                    {
                        ifLogic.HideBlocks(true);
                    }
                    break;
                case LogicalBlockType.Statement:
                case LogicalBlockType.None:
                    break;
            }
        }
    }
}
