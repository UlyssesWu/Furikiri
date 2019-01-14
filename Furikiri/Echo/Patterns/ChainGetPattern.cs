using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a.b.c</example>
    /// </summary>
    class ChainGetPattern : IExpression
    {
        public int Length => (FromGlobal ? 1 : 0) + Members.Count;

        internal ChainGetPattern()
        {
        }

        public ChainGetPattern(short slot, string member)
        {
            Slot = slot;
            Members.Add(member);
        }

        public static ChainGetPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            ChainGetPattern m = null;
            int reg = -1;
            if (codes[i].OpCode == OpCode.GLOBAL)
            {
                reg = codes[i].Registers[0].GetSlot();
                m = new ChainGetPattern {FromGlobal = true};
                i++;
            }

            while (i < codes.Count && codes[i].OpCode == OpCode.GPD)
            {
                var slot = codes[i].Registers[1].GetSlot();
                if (reg == -1 || slot == reg && slot != 0)
                {
                    if (m == null)
                    {
                        m = new ChainGetPattern();
                    }

                    m.Members.Add(codes[i].Data.AsString());
                    m.Slot = codes[i].Registers[0].GetSlot();
                }
                else
                {
                    return m;
                }

                i++;
            }

            return m;
        }

        public bool Terminal
        {
            get => false;
            set { return; }
        }

        public TjsVarType Type { get; private set; } = TjsVarType.Null;
        public short Slot { get; private set; }

        public bool FromGlobal { get; private set; } = false;
        public bool FromThis { get; private set; } = false;

        public List<string> Members = new List<string>();

        public override string ToString()
        {
            if (Members.Count > 0)
            {
                var s = string.Join(".", Members);
                if (FromGlobal)
                {
                    s = "global." + s;
                }

                return s;
            }

            if (FromGlobal)
            {
                return "global";
            }

            return "";
        }
    }
}