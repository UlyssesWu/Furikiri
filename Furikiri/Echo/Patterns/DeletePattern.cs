using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>delete a.b</example>
    /// </summary>
    class DeletePattern : IExpression
    {
        public bool Terminal { get; set; } = true;
        public int Length => 1;
        public TjsVarType Type => TjsVarType.Int;
        public short Slot { get; set; }
        public IExpression Object { get; set; }
        public string Member { get; set; }

        public static DeletePattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            if (codes[i].OpCode == OpCode.DELD)
            {
                DeletePattern d = new DeletePattern
                {
                    Slot = codes[i].GetRegisterSlot(0),
                    Object = context.Expressions[codes[i].GetRegisterSlot(1)],
                    Member = codes[i].Data.AsString()
                };
                return d;
            }

            return null;
        }

        public override string ToString()
        {
            var objName = Object?.ToString();
            if (string.IsNullOrEmpty(objName))
            {
                return $"delete {Member}{Terminal.Term()}";
            }

            return $"delete {Object}.{Member}{Terminal.Term()}";
        }
    }
}