using System.Collections;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

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
                StructureIfElse(b);
            }

            foreach (var b in _context.Blocks)
            {
                StatementPass(statement, b);
            }

            return statement;
        }

        public void StatementPass(BlockStatement entry, Block block)
        {
            foreach (var node in block.Statements)
            {
                switch (node)
                {
                    case GotoExpression _:
                    case ConditionExpression _:
                        break;
                    case Expression expr:
                        entry.AddStatement(new ExpressionStatement(expr));
                        break;
                }
            }
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
                var conditionBlock = loop.Blocks.Last();
                var dw = new DoWhileStatement();
                dw.Condition =
                    (Expression) conditionBlock.Statements.LastOrDefault(s => s is BinaryExpression b && b.IsCompare);
                dw.Body = new BlockStatement(loop.Blocks);
                dw.Break = new BlockStatement(FindBreak(loop));
                dw.Continue = null;

                var cont = loop.Blocks.LastOrDefault();

                if (cont != null)
                {
                    if (cont.Statements.LastOrDefault() is ConditionExpression i)
                    {
                        if (i.JumpTo == loop.Header.Start)
                        {
                            dw.Continue = new BlockStatement(cont);
                        }
                    }

                    if (cont.Statements.LastOrDefault() is GotoExpression g)
                    {
                        if (g.JumpTo == loop.Header.Start)
                        {
                            dw.Continue = new BlockStatement(cont);
                        }
                    }
                }

                loop.Header.Statements.Clear();
                loop.Header.Statements.Add(dw);
                StructureBreakContinue(dw, dw.Continue, dw.Break);
            }
        }

        internal void StructureBreakContinue(Statement stmt, BlockStatement continueBlock, BlockStatement breakBlock)
        {
            switch (stmt)
            {
                case IfStatement ifStmt:
                    if (ifStmt.Else != null && ifStmt.Else != null)
                    {
                        foreach (var el in ifStmt.Else.Statements)
                        {
                            if (el is Statement st)
                            {
                                StructureBreakContinue(st, continueBlock, breakBlock);
                            }
                        }
                    }

                    if (ifStmt.Then != null && ifStmt.Then.Statements.Count > 0)
                    {
                        foreach (var el in ifStmt.Then.Statements)
                        {
                            if (el is Statement st)
                            {
                                StructureBreakContinue(st, continueBlock, breakBlock);
                            }
                        }
                    }

                    break;
            }
        }

        internal bool StructureIfElse(Block block)
        {
            if (block.To.Count != 2)
            {
                return false;
            }

            var trueB = block.To[0];
            var falseB = block.To[1];
            bool hasElse = true;
            if (trueB.To.Count != 1)
            {
                return false;
            }

            if (trueB.To[0] == falseB)
            {
                hasElse = false;
            }
            else
            {
                if (falseB.To.Count != 1)
                {
                    return false;
                }

                if (falseB.To[0] != trueB.To[0])
                {
                    return false;
                }

                hasElse = true;
            }


            IfStatement i = new IfStatement((Expression) block.Statements.LastOrDefault(n => n is Expression),
                new BlockStatement(trueB), null);

            RemoveLastGoto(trueB, trueB.To[0]);
            if (hasElse)
            {
                i.Else = new BlockStatement(falseB);
                RemoveLastGoto(falseB, falseB.To[0]);
            }

            return true;
        }

        private void RemoveLastGoto(Block from, Block to)
        {
            var gt = from.Statements.LastOrDefault(st => st is ConditionExpression || st is GotoExpression);
            if (gt != null)
            {
                from.Statements.Remove(gt);
            }
        }
    }
}