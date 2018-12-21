using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Furikiri.Emit
{
    public class Method
    {
        public CodeObject Object { get; set; }
        public List<Instruction> Instructions { get; set; } = new List<Instruction>();

        public Method(short[] code)
        {
            ParseByteCode(code);
        }

        public Method(CodeObject obj, short[] code)
        {
            ParseByteCode(code);
            Object = obj;
            Resolve(obj);
        }

        /// <summary>
        /// Get code data from context
        /// </summary>
        /// <param name="code"></param>
        public void Resolve(CodeObject code = null)
        {
            if (code == null)
            {
                code = Object;
            }
            foreach (var instruction in Instructions)
            {
                switch (instruction.OpCode)
                {
                    case OpCode.JF:
                    case OpCode.JNF:
                    case OpCode.JMP:
                        instruction.Data = new JumpData(instruction,
                            Instructions.FirstOrDefault(ins =>
                                ins.Offset == instruction.Registers[0].GetSlot() + instruction.Offset));
                        break;
                    case OpCode.CONST:
                    case OpCode.SPD:
                    case OpCode.SPDE:
                    case OpCode.SPDEH:
                    case OpCode.SPDS:
                        var reg = instruction.Registers[1];
                        instruction.Data = new OperandData(instruction, reg, code.Variants[reg.GetSlot()]);
                        break;
                    case OpCode.LORPD:
                    case OpCode.LANDPD:
                    case OpCode.BORPD:
                    case OpCode.BXORPD:
                    case OpCode.BANDPD:
                    case OpCode.SARPD:
                    case OpCode.SALPD:
                    case OpCode.SRPD:
                    case OpCode.ADDPD:
                    case OpCode.SUBPD:
                    case OpCode.MODPD:
                    case OpCode.DIVPD:
                    case OpCode.IDIVPD:
                    case OpCode.MULPD:
                    case OpCode.INCPD:
                    case OpCode.DECPD:
                    case OpCode.GPD:
                    case OpCode.GPDS:
                    case OpCode.DELD:
                    case OpCode.TYPEOFD:
                    case OpCode.CALLD:
                        var reg2 = instruction.Registers[2];
                        instruction.Data = new OperandData(instruction, reg2, code.Variants[reg2.GetSlot()]);
                        break;
                    default:
                        break;
                }
            }
        }

        /// <summary>
        /// Prepare code for write
        /// </summary>
        public void Merge()
        {

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
                sb.Append(ins.Offset.ToString("D8")).Append("\t").Append(ins);
                if (ins.Data != null)
                {
                    sb.Append("\t// ");
                    sb.Append(ins.Data.Comment);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
