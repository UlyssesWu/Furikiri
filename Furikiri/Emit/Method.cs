using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Furikiri.Emit
{
    public class Method
    {
        public string Name => Object.Name;
        public CodeObject Object { get; set; }
        public List<Instruction> Instructions { get; set; } = new List<Instruction>();

        public Method(short[] code)
        {
            ParseByteCode(code);
        }

        public Method(CodeObject obj)
        {
            ParseByteCode(obj.Code);
            Object = obj;
            Resolve();
        }

        public void Compact()
        {
            Resolve();
            //HashSet<OpCode> toBeRemoved = new HashSet<OpCode>();
            Instructions.RemoveAll(instruction =>
                instruction.OpCode == OpCode.NOP || instruction.OpCode == OpCode.DEBUGGER);

            List<Instruction> toBeRemoved = new List<Instruction>();
            for (int i = 0; i < Instructions.Count; i++)
            {
                if (i + 1 < Instructions.Count)
                {
                    if (
                        (Instructions[i].OpCode == OpCode.INC && Instructions[i+1].OpCode == OpCode.DEC || Instructions[i].OpCode == OpCode.DEC && Instructions[i + 1].OpCode == OpCode.INC)
                        && Instructions[i].GetRegisterSlot(0) == Instructions[i+1].GetRegisterSlot(0))
                    {
                        toBeRemoved.Add(Instructions[i]);
                        toBeRemoved.Add(Instructions[i+1]);
                        i++;
                    }
                }
            }

            foreach (var ins in toBeRemoved)
            {
                Instructions.Remove(ins);
            }

            Merge();
        }

        /// <summary>
        /// Get code data from context
        /// </summary>
        public void Resolve()
        {
            var code = Object;
            foreach (var instruction in Instructions)
            {
                switch (instruction.OpCode)
                {
                    case OpCode.JF:
                    case OpCode.JNF:
                    case OpCode.JMP:
                        var jmp = new JumpData(instruction,
                            Instructions.FirstOrDefault(ins =>
                                ins.Offset == instruction.Registers[0].GetSlot() + instruction.Offset));
                        instruction.Data = jmp;
                        jmp.Goto.SetJumpFrom(instruction);
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
        /// Fix offset and prepare code for write
        /// </summary>
        public void Merge()
        {
            var code = Object;
            int offset = 0;

            foreach (var instruction in Instructions)
            {
                instruction.Offset = offset;
                offset += instruction.Size;
            }

            foreach (var instruction in Instructions)
            {
                if (instruction.Data != null)
                {
                    switch (instruction.OpCode)
                    {
                        case OpCode.JF:
                        case OpCode.JNF:
                        case OpCode.JMP:
                            instruction.Registers[0]
                                .SetSlot(instruction.Data.Instruction.Offset - instruction.Offset);
                            break;
                        case OpCode.CONST:
                        case OpCode.SPD:
                        case OpCode.SPDE:
                        case OpCode.SPDEH:
                        case OpCode.SPDS:
                            var data = (OperandData) instruction.Data;
                            var reg = instruction.Registers[1];
                            reg.SetSlot(code.Variants.IndexOf(data.Variant));
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
                            var data2 = (OperandData) instruction.Data;
                            var reg2 = instruction.Registers[2];
                            reg2.SetSlot(code.Variants.IndexOf(data2.Variant));
                            break;
                        default:
                            break;
                    }
                }
            }
        }

        private void ParseByteCode(short[] code)
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

        /// <summary>
        /// Disassemble to assembly code
        /// </summary>
        /// <returns></returns>
        public string ToAssemblyCode()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var ins in Instructions)
            {
                sb.Append(ins.Offset.ToString("D8")).Append("\t").Append(ins);
                if (ins.Data != null)
                {
                    sb.Append(" // ");
                    sb.Append(ins.Data.Comment);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}