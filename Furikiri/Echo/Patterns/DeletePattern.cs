using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>delete a.b</example>
    /// </summary>
    class DeletePattern : IExpression, ITerminal
    {
        public bool Terminal { get; set; } = true;
        public HashSet<int> Write { get; set; }
        public HashSet<int> Read { get; set; }
        public HashSet<int> LiveIn { get; set; }
        public HashSet<int> LiveOut { get; set; }
        public HashSet<int> Dead { get; set; }

        public void ComputeUseDefs()
        {
            Write = new HashSet<int>();
            Read = new HashSet<int>();
            Dead = new HashSet<int>();
            Write.Add(Slot);
        }

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