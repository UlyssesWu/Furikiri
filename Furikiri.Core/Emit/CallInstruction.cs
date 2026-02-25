using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Furikiri.Emit
{
    public class CallInstruction : Instruction
    {
        public FuncParameterExpand ParameterExpandStyle => (FuncParameterExpand)(OpCode.IsInstanceCall()
            ? ((RegisterShort)Registers[3]).Value
            : ((RegisterShort)Registers[2]).Value);

        public CallInstruction(OpCode op) : base(op)
        {
            if (!op.IsCallOrNew())
            {
                throw new ArgumentException("Use CallInstruction without call", nameof(op));
            }
        }

        public override int Size => 1 + ParameterExpandStyle.GetExtraSize() + Registers.Sum(i => i.Size);

        public override short[] ToCodes()
        {
            List<short> output = new List<short>(Size) { OpCode.ToS() };

            void AddCodes(IRegister register)
            {
                switch (register)
                {
                    case RegisterParameter p:
                        output.Add((short)p.ParameterExpand);
                        output.Add((short)p.Slot);
                        break;
                    case RegisterShort s:
                        output.Add(s.Value);
                        break;
                    case RegisterValue v:
                        output.Add((short)v.Slot);
                        break;
                    case RegisterRef r:
                        output.Add((short)r.Slot);
                        break;
                }
            }

            AddCodes(Registers[0]);
            AddCodes(Registers[1]);

            if (OpCode == OpCode.CALLD || OpCode == OpCode.CALLI)
            {
                AddCodes(Registers[2]); //method
                AddCodes(Registers[3]); //paramCount

                if (ParameterExpandStyle == FuncParameterExpand.Expand)
                {
                    output.Add((short)Registers.Skip(4).Count());
                }

                foreach (var register in Registers.Skip(4))
                {
                    AddCodes(register);
                }
            }
            else
            {
                AddCodes(Registers[2]); //paramCount

                if (ParameterExpandStyle == FuncParameterExpand.Expand)
                {
                    output.Add((short)Registers.Skip(3).Count());
                }

                foreach (var register in Registers.Skip(3))
                {
                    AddCodes(register);
                }
            }

            return output.ToArray();
        }
    }
}
