using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// Constant
    /// </summary>
    class ConstPattern : IExpression
    {
        public int Length => 1;

        public ITjsVariant Variant { get; }

        public ConstPattern(ITjsVariant v)
        {
            Variant = v;
        }

        public static ConstPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            if (codes[i].OpCode == OpCode.CONST)
            {
                var data = (OperandData)codes[i].Data;
                return new ConstPattern(data.Variant) {Slot = codes[i].Registers[0].GetSlot()};
            }

            return null;
        }

        public TjsVarType Type => Variant.Type;
        public short Slot { get; private set; }

        public override string ToString()
        {
            return Variant.ToString();
        }
    }
}
