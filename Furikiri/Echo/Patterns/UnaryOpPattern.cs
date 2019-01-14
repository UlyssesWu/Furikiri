using System;
using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    public enum UnaryOp
    {
        Unknown = -1,
        Inc,
        Dec,
        Not,
    }
    /// <summary>
    /// <example>!a</example>
    /// </summary>
    class UnaryOpPattern : IExpression
    {
        public int Length => 1;
        public bool Terminal { get; set; }
        public TjsVarType Type { get; }
        public short Slot { get; }
        public UnaryOp Op { get; set; }
        public IExpression Expression { get; set; }

        public UnaryOpPattern(UnaryOp op)
        {
            Op = op;
        }

        public static UnaryOpPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            UnaryOpPattern u = null;
            switch (codes[i].OpCode)
            {
                case OpCode.INC:
                    u = new UnaryOpPattern(UnaryOp.Inc);
                    u.Expression = context.Expressions[codes[i].GetRegisterSlot(0)];
                    return u;
                case OpCode.DEC:
                    u = new UnaryOpPattern(UnaryOp.Dec);
                    u.Expression = context.Expressions[codes[i].GetRegisterSlot(0)];
                    return u;
                case OpCode.LNOT:
                    u = new UnaryOpPattern(UnaryOp.Not);
                    u.Expression = context.Expressions[codes[i].GetRegisterSlot(0)];
                    return u;
            }

            return u;
        }

        public override string ToString()
        {
            switch (Op)
            {
                case UnaryOp.Inc:
                    return $"{Expression}++";
                case UnaryOp.Dec:
                    return $"{Expression}--";
                    break;
                case UnaryOp.Not:
                    return $"!{Expression}";
                default:
                    break;
            }

            return "";
        }
    }
}
