using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    class ConstPattern : IExpressionPattern
    {
        public int Length { get; }

        public ITjsVariant Variant { get; }

        public ConstPattern(ITjsVariant v)
        {
            Variant = v;
        }

        public static ConstPattern TryMatch(List<Instruction> codes, int i, DecompileContext context)
        {
            if (codes[i].OpCode == OpCode.CONST)
            {
                var data = (OperandData)codes[i].Data;
                return new ConstPattern(data.Variant) {Slot = codes[i].Registers[0].GetSlot()};
            }

            return null;
        }

        public int Slot { get; private set; }

        public override string ToString()
        {
            return base.ToString();
        }
    }
}
