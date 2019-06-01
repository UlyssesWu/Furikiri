using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class IfLogic : ILogical
    {
        public LogicalBlockType ElseType { get; set; } = LogicalBlockType.None;
        public Expression Condition { get; set; }
        public Block ConditionBlock { get; set; }
        public List<Block> Then { get; set; }
        public List<Block> Else { get; set; }
        public Statement ElseStatement { get; set; }
        public ILogical ElseLogic { get; set; }
        
        private void HideBlocks(bool hideConditionBlock = false)
        {
            if (hideConditionBlock)
            {
                ConditionBlock.Hidden = true;
            }
            Then?.SafeHide();
            switch (ElseType)
            {
                case LogicalBlockType.BlockList:
                    Else?.SafeHide();
                    break;
                case LogicalBlockType.Logical:
                    if (ElseLogic is IfLogic ifLogic)
                    {
                        ifLogic.HideBlocks(true);
                    }

                    break;
                case LogicalBlockType.Statement:
                case LogicalBlockType.None:
                    break;
            }
        }

        public Statement ToStatement()
        {
            Statement el = null;
            switch (ElseType)
            {
                case LogicalBlockType.BlockList:
                    if (Else != null)
                    {
                        var elBlockStmt = new BlockStatement(Else, true);
                        if (elBlockStmt.Statements.Count > 0)
                        {
                            el = elBlockStmt;
                        }
                    }

                    break;
                case LogicalBlockType.Statement:
                    if (ElseStatement != null)
                    {
                        el = ElseStatement;
                    }

                    break;
                case LogicalBlockType.Logical:
                    if (ElseLogic != null)
                    {
                        el = ElseLogic.ToStatement();
                    }

                    break;
                case LogicalBlockType.None:
                default:
                    break;
            }

            if (Condition == null && ConditionBlock != null)
            {
                Condition = (ConditionExpression) ConditionBlock.Statements.LastOrDefault(stmt =>
                    stmt is ConditionExpression);
            }

            IfStatement i = new IfStatement(Condition, new BlockStatement(Then, true), el);
            HideBlocks();
            return i;
        }
    }
}