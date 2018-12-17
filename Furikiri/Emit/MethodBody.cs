using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    class MethodBody
    {
        public List<Instruction> Instructions { get; set; } = new List<Instruction>();

        public MethodBody(short[] code)
        {
            ParseByteCode(code);
        }

        public void ParseByteCode(short[] code)
        {
            Instructions.Clear();
            int ptr = 0;
            while (ptr < code.Length)
            {
                var ins = Instruction.Create(code, ptr);
                Instructions.Add(ins);
                ptr += ins.Size;
            }
        }

        public string ToAssemblyCode()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var ins in Instructions)
            {
                sb.Append(ins.Offset.ToString("D8")).Append("\t").Append(ins).AppendLine();
            }

            return sb.ToString();
        }
    }
}
