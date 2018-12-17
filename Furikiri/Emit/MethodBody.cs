using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    class MethodBody
    {
        public List<Instruction> Instructions { get; set; } = new List<Instruction>();

        public MethodBody()
        { }

        public void ParseByteCode(short[] code)
        {
            int ptr = 0;
            while (ptr < code.Length)
            {
                switch ((OpCode)code[ptr])
                {
                }
            }
        }
    }
}
