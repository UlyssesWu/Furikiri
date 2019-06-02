using System.Collections;
using System.Collections.Generic;
using System.Linq;
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

        //public void StatementPass(BlockStatement entry, Block block)
        //{
        //    foreach (var node in block.Statements)
        //    {
        //        switch (node)
        //        {
        //            case GotoExpression _:
        //            case ConditionExpression _:
        //                break;
        //            case Expression expr:
        //                entry.AddStatement(new ExpressionStatement(expr));
        //                break;
        //        }
        //    }
        //}

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
                var conditionBlock = loop.Blocks.Last();

                var dw = new DoWhileLogic();
                var lastExpr = conditionBlock.Statements.LastOrDefault();
                if (lastExpr is ConditionExpression condition) //Compare inside condition
                {
                    if (condition.Condition is BinaryExpression b && b.IsCompare)
                    {
                        dw.Condition = b;
                    }
                }
                else if (lastExpr is BinaryExpression b && b.IsCompare)
                {
                    dw.Condition = b;
                }
                else
                {
                    dw.Condition =
                        (Expression) conditionBlock.Statements.LastOrDefault(s =>
                            s is BinaryExpression b2 && b2.IsCompare);
                }

                dw.Body = new List<Block>(loop.Blocks);
                dw.Body.Remove(conditionBlock);
                dw.Break = FindBreak(loop);
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

        internal bool StructureIfElse(Block block, out IfLogic outIf)
        {
            outIf = null;
            if (block.To.Count != 2)
            {
                return false;
            }

            var cond = (ConditionExpression) block.Statements.LastOrDefault(stmt => stmt is ConditionExpression);
            if (cond == null)
            {
                return false;
            }

            var loop = _context.LoopSet.FirstOrDefault(l => l.Contains(block));

            var thenBlock = block.To[0];
            var elseBlock = block.To[1];
            var logic = new IfLogic {ConditionBlock = block, Condition = cond};

            if (thenBlock.To.Count == 2) //TODO: can be 2 - inner If
            {
                return false;
            }

            if (thenBlock.To.Count != 1)
            {
                return false;
            }

            if (thenBlock.To[0] == elseBlock)
            {
                //hasElse = false;
            }
            else
            {
                if (loop != null)
                {
                    if (elseBlock.Start >= loop.Exit)
                    {
                        logic.Else.Type = LogicalBlockType.Statement;
                        logic.Else.Statement = new BreakStatement();
                    }
                }
                else
                {
                    if (elseBlock.To.Count == 2) //can be inner if
                    {
                        if (StructureIfElse(elseBlock, out IfLogic innerIf))
                        {
                            logic.Else.Type = LogicalBlockType.Logical;
                            logic.Else.Logic = innerIf;
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
                        logic.Else.Blocks = new List<Block> {elseBlock};
                        RemoveLastGoto(elseBlock, elseBlock.To[0]);
                    }
                    else
                    {
                        return false;
                    }
                }
            }

            logic.Then.Blocks = new List<Block> {thenBlock};
            RemoveLastGoto(thenBlock, thenBlock.To[0]);

            outIf = logic;
            return true;
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
    }
}