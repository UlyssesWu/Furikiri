using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    public enum BinaryOp
    {
        Unknown = -1,
        Assign,
        Add,
        Sub,
        Mul,
        Div,
        Idiv,
        Equal,
        NotEqual,
        Congruent,
        NotCongruent,
    }

    /// <summary>
    /// <example>a+b</example>
    /// </summary>
    class BinaryOpPattern : IExpressionPattern
    {
        public int Length => 1;
        public IExpressionPattern Left { get; set; }
        public IExpressionPattern Right { get; set; }
        public BinaryOp Op { get; set; }

        public bool Terminal { get; set; }

        /// <summary>
        /// Left Type
        /// </summary>
        public TjsVarType Type => Left.Type != TjsVarType.Null? Left.Type : Right.Type;

        public static BinaryOpPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            BinaryOpPattern b = new BinaryOpPattern();
            switch (codes[i].OpCode)
            {
                case OpCode.ADD:
                case OpCode.SUB:
                case OpCode.MUL:
                case OpCode.DIV:
                case OpCode.IDIV:
                {
                    context.PopExpressionPatterns(codes[i].GetRelatedSlots());
                    var exps = context.Expressions;
                    b.Slot = codes[i].GetRegisterSlot(0);
                    b.Left = exps[b.Slot];
                    b.Right = exps[codes[i].GetRegisterSlot(1)];
                    b.Op = GetOp(codes[i].OpCode);
                    return b;
                }
                case OpCode.SPDS: //this.a = b;
                {
                    context.PopExpressionPatterns();
                    var exps = context.Expressions;
                    var thisSlot = codes[i].GetRegisterSlot(0);
                    var leftSlot = (OperandData)codes[i].Data;
                    var rightSlot = codes[i].GetRegisterSlot(2);
                    b.Right = exps[rightSlot];
                    b.Left = new ChainGetPattern(thisSlot, (TjsString)leftSlot.Variant);
                    b.Terminal = true;
                    return b;
                }
                case OpCode.CP: //var a = b;
                {
                    var dst = codes[i].GetRegisterSlot(0);
                    var src = codes[i].GetRegisterSlot(1);
                    if (dst < Const.ThisProxy && src > Const.Resource)
                    {
                            //set var
                            context.PopExpressionPatterns();
                            var exps = context.Expressions;
                            LocalPattern l = new LocalPattern(true, dst){Type = exps[src].Type};
                            b.Left = l;
                            b.Right = exps[src];
                            b.Op = BinaryOp.Assign;
                            context.Expressions[dst] = l;
                            b.Terminal = true;
                            return b;
                    }
                    return null;
                }
            }
            return null;
        }

        public static BinaryOp GetOp(OpCode code)
        {
            switch (code)
            {
                case OpCode.SPDS:
                    return BinaryOp.Assign;
                case OpCode.ADD:
                    return BinaryOp.Add;
                case OpCode.SUB:
                    return BinaryOp.Sub;
                case OpCode.MUL:
                    return BinaryOp.Mul;
                case OpCode.DIV:
                    return BinaryOp.Div;
                case OpCode.IDIV:
                    return BinaryOp.Idiv;
                default:
                    break;
            }

            return BinaryOp.Unknown;
        }

        public short Slot { get; private set; }

        public override string ToString()
        {
            return $"{Left.ToString()} {Op.ToSymbol()} {Right.ToString()}{Terminal.TermSymbol()}";
        }
    }
}
