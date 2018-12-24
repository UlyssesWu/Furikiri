using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a+b</example>
    /// </summary>
    class BinaryOpPattern : IExpressionPattern
    {
        public int Length => 1;

        public static BinaryOpPattern TryMatch(List<Instruction> codes, int i, DecompileContext context)
        {
            if (codes[i].OpCode == OpCode.ADD)
            {
                Dictionary<int, IExpressionPattern> expressions = context.PopExpressionPatterns();
                BinaryOpPattern b = new BinaryOpPattern();
                b.Slot = codes[i].Registers[0].GetSlot();
                b.Expressions[0] = expressions[b.Slot];
                b.Expressions[1] = expressions[codes[i].Registers[1].GetSlot()];
            }

            return null;
        }

        public int Slot { get; private set; }

        public IExpressionPattern[] Expressions = new IExpressionPattern[2];

        public override string ToString()
        {
            return $"{Expressions[0].ToString()} + {Expressions[1].ToString()}";
        }
    }
}
