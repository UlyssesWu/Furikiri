using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// For / While / DoWhile
    /// </summary>
    class LoopPattern : IBranch
    {
        public bool Terminal { get; set; } = true;
        public int Length { get; private set; }
        public BranchType BranchType { get; set; }
        public Instruction Entry { get; set; }
        public Instruction Exit { get; set; }
        public IExpression ForOperand { get; set; }
        public IExpression Condition { get; set; }
        public IExpression ForOperation { get; set; }
        public List<Block> Content { get; set; } = new List<Block>();
        public List<IPattern> InsContent { get; set; } = new List<IPattern>();

        public static LoopPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            LoopPattern loop = null;
            if (codes[i].JumpedFrom != null && codes[i].JumpedFrom.Count > 0 &&
                codes[i].JumpedFrom[0].Offset > codes[i].Offset)
            {
                context.PopExpressionPatterns();
                loop = new LoopPattern();
                var jumped = codes[i].JumpedFrom[0];
                Instruction jumped2 = null;
                var jmpIdx = codes.IndexOf(jumped);
                if (jmpIdx < codes.Count - 1)
                {
                    jumped2 = codes[jmpIdx + 1].JumpedFrom.Find(ins => ins.Offset < jumped.Offset);
                }

                if (i >= 1 && codes[i - 1].OpCode == OpCode.CP &&
                    codes[codes.IndexOf(codes[i].JumpedFrom[0]) - 1].GetRegisterSlot(0) ==
                    codes[i - 1].GetRegisterSlot(0) &&
                    !context.ContainsBranch(i, BranchType.For)) //for
                {
                    loop.BranchType = BranchType.For;
                    loop.ForOperand =
                        (IExpression) context.Patterns.Last(p => p is BinaryOpPattern bop && bop.Op == BinaryOp.Assign);
                    loop.ForOperand.Terminal = false;
                    loop.Entry = codes[i];
                    loop.Exit = jumped;
                    loop.Length = loop.CountLength(codes);
                    context.AddBranch(i, BranchType.For);

                    var binOp = BinaryOpPattern.Match(codes, i, context);
                    if (binOp != null)
                    {
                        if (codes[i + binOp.Length].OpCode.IsJump(true))
                        {
                            //has condition
                            binOp.Terminal = false;
                            loop.Condition = binOp;
                            loop.DetectContent(codes, i + binOp.Length + 1, context);
                        }
                    }
                    else
                    {
                        loop.DetectContent(codes, i, context);
                    }

                    loop.ForOperation = (IExpression) loop.Content.LastOrDefault(p => p is IExpression);
                }

                else if (jumped2 != null) //while
                {
                    loop.BranchType = BranchType.While;
                }
                else
                {
                    loop.BranchType = BranchType.DoWhile;
                }
            }

            return loop;
        }

        private void DetectContent(List<Instruction> instructions, int start, DecompileContext context)
        {
            int offset = start;
            while (offset < instructions.IndexOf(Exit))
            {
                if (context.DetectPattern(instructions, offset, out var pattern))
                {
                    offset += pattern.Length;
                    InsContent.Add(pattern);
                }
                else
                {
                    offset++;
                }
            }

            context.InstructionQueue.Clear();
        }

        private int CountLength(List<Instruction> codes)
        {
            var start = codes.IndexOf(Entry);
            var end = codes.IndexOf(Exit);
            return end - start;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            switch (BranchType)
            {
                case BranchType.For:
                    sb.AppendLine($"for ({ForOperand}; {Condition}; {ForOperation})");
                    sb.AppendLine("{");
                    foreach (var pattern in InsContent)
                    {
                        sb.AppendLine($"    {pattern}");
                    }

                    sb.AppendLine("}");
                    break;
                case BranchType.While:
                    sb.AppendLine($"while ({Condition})");
                    sb.AppendLine("{");
                    foreach (var pattern in InsContent)
                    {
                        sb.AppendLine($"    {pattern}");
                    }

                    sb.AppendLine("}");
                    break;
                case BranchType.DoWhile:
                    sb.AppendLine("do");
                    sb.AppendLine("{");
                    foreach (var pattern in InsContent)
                    {
                        sb.AppendLine($"    {pattern}");
                    }

                    sb.AppendLine("}");
                    sb.AppendLine($"while ({Condition})");
                    break;
                default:
                    break;
            }

            return sb.ToString();
        }
    }
}