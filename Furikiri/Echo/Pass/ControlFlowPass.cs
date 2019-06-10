using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    class ControlFlowPass : IPass
    {
        private DecompileContext _context;

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            _context = context;
            _context.LoopSetSort();

            IntervalAnalysisDoWhilePass();

            foreach (var b in _context.Blocks)
            {
                if (StructureIfElse(b, out var logic))
                {
                    if (logic.Else.IsBreak)
                    {
                        //can be while!
                        var loop = context.LoopSet.FirstOrDefault(l => l.Header == b);
                        if (loop != null && loop.LoopLogic is DoWhileLogic dw)
                        {
                            dw.IsWhile = true;
                            dw.Condition = logic.Condition;
                            dw.Body = logic.Then.Blocks;
                        }
                        else
                        {
                            b.Statements.Replace(logic.Condition, logic.ToStatement());
                        }
                    }
                    else
                    {
                        b.Statements.Replace(logic.Condition, logic.ToStatement());
                    }
                }
            }


            return statement;
        }

        public Expression FindCondition(Loop l)
        {
            if (l.Blocks.Count <= 0)
            {
                return null;
            }

            var last = l.Blocks.Last();
            var exps = last.Statements;
            if (exps == null || exps.Count <= 0)
            {
                return null;
            }

            return (Expression) exps.LastOrDefault(s => s is BinaryExpression b && b.IsCompare);
        }

        private Block FindBreak(Loop loop)
        {
            BitArray b = new BitArray(_context.ExitBlock.PostDominator.Length);
            b.SetAll(true);
            foreach (var block in loop.Blocks)
            {
                b.And(block.PostDominator);
                b[block.Id] = false; //block after break can not still stay in the loop
            }

            var id = b.FirstIndexOf(true);
            if (id >= 0)
            {
                return _context.Blocks[id];
            }

            return null;
        }

        internal void IntervalAnalysisDoWhilePass()
        {
            _context.LoopSet.ForEach(l => l.Blocks.Sort((b1, b2) => b1.Start - b2.Start));

            foreach (var loop in _context.LoopSet)
            {
                var lastBlock = loop.Blocks.Last();
                var dw = new DoWhileLogic();
                dw.Break = FindBreak(loop);

                Block conditionBlock = null;
                if (lastBlock.Statements.GetCondition() is ConditionExpression lastCondi && lastCondi.TrueBranch == loop.Header.Start)
                {
                    dw.Condition = lastCondi;
                    conditionBlock = lastBlock;
                }
                else if (lastBlock.Statements.Count == 1 && lastBlock.Statements[0] is GotoExpression && loop.Header.Statements.GetCondition() is ConditionExpression condi && condi.FalseBranch == dw.Break.Start)
                {
                    dw.IsWhile = true;
                    dw.Condition = condi;
                    conditionBlock = loop.Header;
                }

                dw.Body = new List<Block>(loop.Blocks);
                dw.Body.Remove(conditionBlock);
                dw.Continue = null;

                var cont = loop.Blocks.LastOrDefault();

                if (cont != null)
                {
                    if (cont.Statements.LastOrDefault() is ConditionExpression i)
                    {
                        if (i.JumpTo == loop.Header.Start)
                        {
                            dw.Continue = cont;
                        }
                    }

                    if (cont.Statements.LastOrDefault() is GotoExpression g)
                    {
                        if (g.JumpTo == loop.Header.Start)
                        {
                            dw.Continue = cont;
                        }
                    }
                }

                ILogical logic = dw;
                if (DoWhileToFor(loop, dw, out var f))
                {
                    logic = f;
                }
                else
                {
                    foreach (var bodyBlock in dw.Body)
                    {
                        StructureBreakContinue(bodyBlock, dw.Continue, dw.Break);
                    }
                }

                loop.LoopLogic = logic;
            }
        }

        internal bool DoWhileToFor(Loop loop, DoWhileLogic dw, out ForLogic f)
        {
            f = null;
            var idx = _context.Blocks.IndexOf(loop.Blocks.First());
            if (idx < 1)
            {
                return false;
            }

            var prev = _context.Blocks[idx - 1];
            var lastAssign =
                (BinaryExpression) prev.Statements.LastOrDefault(
                    n => n is BinaryExpression b && b.Op == BinaryOp.Assign);
            //if (lastAssign == null || !(lastAssign.Left is LocalExpression l))
            if (lastAssign == null)
            {
                return false;
            }

            var l = lastAssign.Left;

            Expression step = null;
            //the increment statement can be unary or binary
            UnaryExpression step1 = (UnaryExpression) dw.Continue.Statements
                .LastOrDefault(n => (n is UnaryExpression));
            if (step1 != null && (step1.Op == UnaryOp.Inc || step1.Op == UnaryOp.Dec) && step1.Target == l)
            {
                step = step1;
            }
            else
            {
                var step2 = (BinaryExpression) dw.Continue.Statements
                    .LastOrDefault(n => (n is BinaryExpression));
                if (step2 != null)
                {
                    step = step2;
                }
                else
                {
                    return false;
                }
            }

            dw.Continue.Hidden = true;

            f = new ForLogic {Initializer = lastAssign, Increment = step, Condition = dw.Condition, Body = dw.Body};
            prev.Statements.Remove(lastAssign);

            foreach (var bodyBlock in f.Body)
            {
                StructureBreakContinue(bodyBlock, dw.Continue, dw.Break);
            }

            return true;
        }

        internal void StructureBreakContinue(Block b, Block continueBlock, Block breakBlock)
        {
            for (var i = 0; i < b.Statements.Count; i++)
            {
                var node = b.Statements[i];
                if (node is GotoExpression g)
                {
                    if (continueBlock != null && g.JumpTo == continueBlock.Start)
                    {
                        b.Statements[i] = new ContinueStatement();
                    }
                    else if (breakBlock != null && g.JumpTo == breakBlock.Start)
                    {
                        b.Statements[i] = new BreakStatement();
                    }
                }
            }
        }


        //internal void StructureBreakContinue(Statement stmt, Block continueBlock, Block breakBlock)
        //{
        //    switch (stmt)
        //    {
        //        case IfStatement ifStmt:
        //            if (ifStmt.Else != null && ifStmt.Else != null)
        //            {
        //                foreach (var el in ifStmt.Else.Statements)
        //                {
        //                    if (el is Statement st)
        //                    {
        //                        StructureBreakContinue(st, continueBlock, breakBlock);
        //                    }
        //                }
        //            }

        //            if (ifStmt.Then != null && ifStmt.Then.Statements.Count > 0)
        //            {
        //                foreach (var el in ifStmt.Then.Statements)
        //                {
        //                    if (el is Statement st)
        //                    {
        //                        StructureBreakContinue(st, continueBlock, breakBlock);
        //                    }
        //                }
        //            }

        //            break;
        //    }
        //}

        //internal bool StructureIfElse(Block block, out IfStatement st)
        //{
        //    st = null;
        //    if (block.To.Count != 2)
        //    {
        //        return false;
        //    }

        //    var cond = (ConditionExpression) block.Statements.LastOrDefault(stmt => stmt is ConditionExpression);

        //    BlockStatement then = null;
        //    BlockStatement el = null;
        //    var loop = _context.LoopSet.FirstOrDefault(l => l.Contains(block));

        //    var thenBlock = block.To[0];
        //    var elseBlock = block.To[1];
        //    bool hasElse = false;
        //    if (thenBlock.To.Count != 1)
        //    {
        //        return false;
        //    }

        //    if (thenBlock.To[0] == elseBlock)
        //    {
        //        hasElse = false;
        //    }
        //    else
        //    {
        //        if (loop != null)
        //        {
        //            if (elseBlock.Start >= loop.Exit)
        //            {
        //                el = new BlockStatement();
        //                el.AddStatement(new BreakStatement());
        //                el.Resolved = true;
        //            }
        //        }
        //        else
        //        {
        //            if (elseBlock.To.Count != 1)
        //            {
        //                return false;
        //            }

        //            if (elseBlock.To[0] != thenBlock.To[0])
        //            {
        //                return false;
        //            }

        //            hasElse = true;
        //        }
        //    }

        //    if (hasElse)
        //    {
        //        if (StructureIfElse(elseBlock, out IfStatement innerIf))
        //        {
        //            el = innerIf;
        //        }
        //        else
        //        {
        //            el = new BlockStatement(elseBlock);
        //            RemoveLastGoto(elseBlock, elseBlock.To[0]);
        //            el.ResolveFromBlocks();
        //        }
        //    }

        //    then = new BlockStatement(thenBlock);
        //    RemoveLastGoto(thenBlock, thenBlock.To[0]);
        //    then.ResolveFromBlocks();

        //    IfStatement i = new IfStatement(cond, then, el);

        //    st = i;
        //    return true;
        //}

        private bool GetThenElseBlock(ConditionExpression condition, List<Block> blocks, out Block then,
            out Block @else)
        {
            if (blocks.Count < 2)
            {
                then = null;
                @else = null;
                return false;
            }

            Block toBlock = blocks.FirstOrDefault(b => b.Start == condition.JumpTo);
            Block elBlock = blocks.FirstOrDefault(b => b.Start == condition.ElseTo);
            if (toBlock != null && elBlock != null)
            {
                if (condition.JumpIf)
                {
                    then = toBlock;
                    @else = elBlock;
                }
                else
                {
                    then = elBlock;
                    @else = toBlock;
                }

                return true;
            }

            then = blocks[0];
            @else = blocks[1];
            return false;
        }

        private void MergeIfCondition(IfLogic logic, ConditionExpression condition)
        {
            //recursive to final condition and go back
            var trueBlock = _context.BlockTable[condition.TrueBranch];
            var falseBlock = _context.BlockTable[condition.FalseBranch];
            Expression exp = null;
            MergeIfCondition(condition, logic.ConditionBlock, logic.PostDominator, ref exp, ref trueBlock, ref falseBlock);
            if (exp != null)
            {
                if (logic.Condition is ConditionExpression condi)
                {
                    condi.Condition = exp;
                }
                else
                {
                    logic.Condition = exp;
                }
                logic.Then.Blocks = new List<Block>{ trueBlock };
                logic.Else.Blocks = new List<Block>{ falseBlock};
            }
        }

        private Block FindIfPostDominator(Block conditionBlock)
        {
            BitArray d = new BitArray(conditionBlock.PostDominator);
            FindPostDominator(conditionBlock);

            return _context.Blocks.Find(b => b.Id == d.FirstIndexOf(true, conditionBlock.Id + 1));

            void FindPostDominator(Block condition)
            {
                if (!condition.Statements.IsCondition())
                {
                    return;
                }

                if (condition.Id == d.FirstIndexOf(true, conditionBlock.Id + 1))
                {
                    return;
                }

                d.And(condition.PostDominator);

                foreach (var block in condition.To)
                {
                    FindPostDominator(block);
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="condition">current condition</param>
        /// <param name="conditionBlock">current condition block</param>
        /// <param name="dominator">if dominator</param>
        /// <param name="merge">merged if condition expression</param>
        /// <param name="then">assumed if true block</param>
        /// <param name="else">actual else block</param>
        /// <returns></returns>
        private bool MergeIfCondition(ConditionExpression condition, Block conditionBlock, Block dominator, ref Expression merge, ref Block then, ref Block @else)
        {
            if (condition.TrueBranch == dominator.Start)
            {
                condition = (ConditionExpression)condition.Invert();
                then = _context.BlockTable[condition.TrueBranch];
            }

            var trueBlock = _context.BlockTable[condition.TrueBranch];
            var falseBlock = _context.BlockTable[condition.FalseBranch];
            var trueIsContent = IsBranchContent(trueBlock);
            var falseIsContent = IsBranchContent(falseBlock);

            if (trueBlock != then && falseBlock != dominator) //it's else if, can not merge
            {
                @else = conditionBlock;
                return false;
            }

            if (!trueIsContent && dominator == falseBlock)
            {
                var trueCondition = trueBlock.Statements.GetCondition();
                if (MergeIfCondition(trueCondition, trueBlock, dominator, ref merge, ref then, ref @else))
                {
                    trueBlock.Hidden = true;
                    merge = condition.Condition.And(merge);
                    return true;
                }
            }

            if (!falseIsContent && then == trueBlock)
            {
                var falseCondition = falseBlock.Statements.GetCondition();
                if (MergeIfCondition(falseCondition, falseBlock, dominator, ref merge, ref then, ref @else))
                {
                    falseBlock.Hidden = true;
                    merge = condition.Condition.Or(merge);
                    return true;
                }
            }

            if (trueBlock == then && falseBlock == dominator) //final condition
            {
                @else = dominator;
                merge = condition;
                return true;
            }

            @else = falseBlock;
            return false;
        }

        private bool IsBranchContent(Block block)
        {
            return block.To.Count < 2 ||
                   (block.From.Count > 1 && block.From.All(b => b.Statements.IsCondition())) ||  //it's hard to merge sth. like A & (B || C)
                   block.From.Any(b => !b.Statements.IsCondition());
        }

        private void RemoveLastGoto(Block from, Block to)
        {
            //TODO: avoid remove essential break/continue;
            var gt = from.Statements.LastOrDefault(st =>
                st is ConditionExpression || st is GotoExpression || st is ContinueStatement || st is BreakStatement);
            if (gt != null)
            {
                from.Statements.Remove(gt);
            }
        }

        internal bool StructureIfElse(Block block, out IfLogic outIf)
        {
            outIf = null;
            if (block.To.Count != 2)
            {
                return false;
            }

            var cond = (ConditionExpression)block.Statements.LastOrDefault(stmt => stmt is ConditionExpression);
            if (cond == null)
            {
                return false;
            }

            var loop = _context.LoopSet.FirstOrDefault(l => l.Contains(block));
            if (loop?.LoopLogic is IConditional conditionLogic)
            {
                if (conditionLogic.Condition == cond)
                {
                    return false;
                }
            }

            var postDominator = FindIfPostDominator(block);
            //if (cond.TrueBranch == postDominator.Start)
            //{
            //    cond = (ConditionExpression)cond.Invert();
            //}

            Block thenBlock = block.To.FirstOrDefault(b => b.Start == cond.TrueBranch);
            Block elseBlock = block.To.FirstOrDefault(b => b.Start == cond.FalseBranch);
            if (thenBlock == null || elseBlock == null)
            {
                thenBlock = block.To[0];
                elseBlock = block.To[1];
            }
            
            var logic = new IfLogic { ConditionBlock = block, Condition = cond, PostDominator = postDominator };


            logic.Then.Blocks = new List<Block> {thenBlock};
            logic.Else.Blocks = new List<Block> {elseBlock};

            bool elseIsBreak = false;
            if (loop != null)
            {
                if (elseBlock.Start >= loop.Exit)
                {
                    elseIsBreak = true;
                }
            }

            //if (thenBlock.To.Count == 2) //TODO: can be 2 - inner If
            if (!IsBranchContent(thenBlock) || (!IsBranchContent(elseBlock) && !elseIsBreak)) //|| !IsBranchContent(elseBlock)
            {
                MergeIfCondition(logic, cond);
                //if (thenBlock.Statements.IsCondition())
                //{
                //}
                //else
                //{
                //    return false;
                //}
            }

            //if (thenBlock.To.Count != 1)
            //{
            //    return false;
            //}

            if (logic.Then.Blocks.Count > 0)
            {
                thenBlock = logic.Then.Blocks[0];
            }
            else
            {
                logic.Then.Blocks = new List<Block> { thenBlock };
            }

            if (logic.Else.Blocks.Count > 0)
            {
                elseBlock = logic.Else.Blocks[0];
            }

            if (thenBlock.To[0] == elseBlock)
            {
                logic.Else.Type = LogicalBlockType.None;
            }
            else
            {
                if (elseIsBreak)
                {
                    logic.Else.Type = LogicalBlockType.Statement;
                    logic.Else.Statement = new BreakStatement();
                }
                else
                {
                    if (elseBlock.To.Count == 2) //can be inner if
                    {
                        if (StructureIfElse(elseBlock, out IfLogic innerIf))
                        {
                            logic.Else.Type = LogicalBlockType.Logical;
                            logic.Else.Logic = innerIf;
                            innerIf.ParentIf = logic;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else if (elseBlock.To.Count == 1)
                    {
                        if (elseBlock.To[0] != thenBlock.To[0])
                        {
                            return false;
                        }

                        //hasElse = true;
                        logic.Else.Type = LogicalBlockType.BlockList;
                        logic.Else.Blocks = new List<Block> { elseBlock };
                        RemoveLastGoto(elseBlock, elseBlock.To[0]);
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            RemoveLastGoto(thenBlock, thenBlock.To[0]);

            outIf = logic;
            return true;
        }
    }
}