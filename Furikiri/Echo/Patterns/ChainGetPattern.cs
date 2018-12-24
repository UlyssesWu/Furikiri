using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a.b.c</example>
    /// </summary>
    class ChainGetPattern : ITjsPattern
    {
        public int Length => (FromGlobal ? 1 : 0) + Members.Count;

        public static ChainGetPattern TryMatch(List<Instruction> codes, int i, DecompileContext context)
        {
            ChainGetPattern m = null;
            int reg = -1;
            if (codes[i].OpCode == OpCode.GLOBAL)
            {
                reg = codes[i].Registers[0].GetSlot();
                m = new ChainGetPattern {FromGlobal = true};
                i++;
            }
            while (codes[i].OpCode == OpCode.GPD)
            {
                var slot = codes[i].Registers[1].GetSlot();
                if (reg == -1 || slot == reg && slot != 0)
                {
                    var data = (OperandData)codes[i].Data;
                    var str = (TjsString)data.Variant;
                    if (m == null)
                    {
                        m = new ChainGetPattern();
                    }
                    m.Members.Add(str);
                }
                else
                {
                    return m;
                }

                i++;
            }

            return m;
        }

        public bool FromGlobal { get; private set; } = false;

        public List<string> Members = new List<string>();
    }
}
