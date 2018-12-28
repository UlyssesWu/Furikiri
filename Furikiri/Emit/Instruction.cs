using System.Collections.Generic;
using System.Linq;
using static Furikiri.Emit.OpCode;

// ReSharper disable PossibleMultipleEnumeration

namespace Furikiri.Emit
{
    /// <summary>
    /// TJS ASM Instruction
    /// </summary>
    public class Instruction
    {
        public OpCode OpCode { get; set; }
        public List<IRegister> Registers { get; set; } = new List<IRegister>();
        public int Offset { get; set; }
        public int Size => 1 + Registers.Sum(i => i.Size);
        public IRegisterData Data { get; set; }
        public List<Instruction> JumpedFrom { get; set; }

        public Instruction(OpCode op)
        {
            OpCode = op;
        }

        public IRegister this[int i] => Registers[i];

        /// <summary>
        /// Get slot of target register
        /// </summary>
        /// <param name="register"></param>
        /// <returns></returns>
        internal short GetRegisterSlot(int register)
        {
            return Registers[register].GetSlot();
        }

        internal List<short> GetRelatedSlots()
        {
            return Registers.Where(r => r is RegisterRef).Select(rr => rr.GetSlot()).ToList();
        }

        /// <summary>
        /// Get the max slot in this instruction
        /// </summary>
        internal short TopSlot
        {
            get
            {
                var rs = from r in Registers where r is RegisterRef rr select (RegisterRef) r;
                if (rs.Any())
                {
                    return rs.Max(r => r.Slot);
                }

                return Const.ThisProxy;
            }
        }

        public void SetJumpFrom(Instruction ins)
        {
            if (JumpedFrom == null)
            {
                JumpedFrom = new List<Instruction>();
            }

            JumpedFrom.Add(ins);
        }

        public static Instruction Create(in short[] code, int index)
        {
            Instruction ins = null;
            var op = (OpCode) code[index];
            switch (op)
            {
                //0
                case NOP:
                case NF:
                case RET:
                case EXTRY:
                case REGMEMBER:
                case DEBUGGER:
                    ins = new Instruction(op);
                    break;
                //1
                //%
                case CL:
                case LNOT:
                case TT:
                case TF:
                case SETF:
                case SETNF:
                case INC:
                case DEC:
                case BNOT:
                case TYPEOF:
                case EVAL:
                case EEXP:
                case ASC:
                case CHR:
                case NUM:
                case CHS:
                case INV:
                case CHKINV:
                case INT:
                case REAL:
                case STR:
                case OCTET:
                case SRV:
                case THROW:
                case GLOBAL:
                    ins = new Instruction(op)
                        {Registers = {new RegisterRef(code[index + 1])}};
                    break;
                //instant
                case JF:
                case JNF:
                case JMP:
                    ins = new Instruction(op)
                        {Registers = {new RegisterShort(code[index + 1])}};
                    break;
                //2
                //%, %
                case ENTRY:
                    ins = new Instruction(op)
                        {Registers = {new RegisterShort(code[index + 1]), new RegisterRef(code[index + 2])}};
                    break;
                //%, *
                case CONST:
                    ins = new Instruction(op)
                        {Registers = {new RegisterRef(code[index + 1]), new RegisterValue(code[index + 2])}};
                    break;
                //%, %
                case CP:
                case CCL:
                case CEQ:
                case CDEQ:
                case CLT:
                case CGT:
                case INCP:
                case DECP:
                case LOR:
                case LAND:
                case BOR:
                case BXOR:
                case BAND:
                case SAR:
                case SAL:
                case SR:
                case ADD:
                case SUB:
                case MOD:
                case DIV:
                case IDIV:
                case MUL:
                case CHKINS:
                case SETP:
                case GETP:
                case CHGTHIS:
                case ADDCI:
                    ins = new Instruction(op)
                        {Registers = {new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2])}};
                    break;

                //3
                //%, %, %
                case LORP:
                case LANDP:
                case BORP:
                case BXORP:
                case BANDP:
                case SARP:
                case SALP:
                case SRP:
                case ADDP:
                case SUBP:
                case MODP:
                case DIVP:
                case IDIVP:
                case MULP:
                case GPI:
                case SPI:
                case SPIE:
                case GPIS:
                case SPIS:
                case DELI:
                case INCPI:
                case DECPI:
                case TYPEOFI:
                    ins = new Instruction(op)
                    {
                        Registers =
                        {
                            new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]),
                            new RegisterRef(code[index + 3])
                        }
                    };
                    break;

                //%, %, *
                case GPD:
                case GPDS:
                case DELD:
                case INCPD:
                case DECPD:
                case TYPEOFD:
                    ins = new Instruction(op)
                    {
                        Registers =
                        {
                            new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]),
                            new RegisterValue(code[index + 3])
                        }
                    };
                    break;
                //%, *, %
                case SPD:
                case SPDE:
                case SPDEH:
                case SPDS:
                    ins = new Instruction(op)
                    {
                        Registers =
                        {
                            new RegisterRef(code[index + 1]), new RegisterValue(code[index + 2]),
                            new RegisterRef(code[index + 3])
                        }
                    };
                    break;

                //%, %, %, %
                case LORPI:
                case LANDPI:
                case BORPI:
                case BXORPI:
                case BANDPI:
                case SARPI:
                case SALPI:
                case SRPI:
                case ADDPI:
                case SUBPI:
                case MODPI:
                case DIVPI:
                case IDIVPI:
                case MULPI:
                    ins = new Instruction(op)
                    {
                        Registers =
                        {
                            new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]),
                            new RegisterRef(code[index + 3]), new RegisterRef(code[index + 4])
                        }
                    };
                    break;

                //%, %, *, %
                case LORPD:
                case LANDPD:
                case BORPD:
                case BXORPD:
                case BANDPD:
                case SARPD:
                case SALPD:
                case SRPD:
                case ADDPD:
                case SUBPD:
                case MODPD:
                case DIVPD:
                case IDIVPD:
                case MULPD:
                    ins = new Instruction(op)
                    {
                        Registers =
                        {
                            new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]),
                            new RegisterValue(code[index + 3]), new RegisterRef(code[index + 4])
                        }
                    };
                    break;

                //special
                case CALL:
                case CALLD:
                case CALLI:
                case NEW:
                    ins = new Instruction(op)
                        {Registers = {new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2])}};
                    int idx = index + 3;
                    int paramCount;
                    if (op == CALLI || op == CALLD)
                    {
                        ins.Registers.Add(new RegisterRef(code[idx]));
                        idx++;
                        ins.Registers.Add(new RegisterShort(code[idx]));
                        paramCount = code[idx];
                    }
                    else
                    {
                        ins.Registers.Add(new RegisterShort(code[idx]));
                        paramCount = code[idx];
                    }

                    if (paramCount == -1) //omit(ignore) param
                    {
                        break;
                    }
                    else if (paramCount == -2) //expand param
                    {
                        idx++;
                        paramCount = code[idx];
                        for (int i = 0; i < paramCount; i++)
                        {
                            idx++;
                            ins.Registers.Add(new RegisterParameter(code[idx + 1])
                                {ParameterExpand = (FuncParameterExpand) code[idx]});
                            idx++;
                        }
                    }
                    else //normal param
                    {
                        for (int i = 0; i < paramCount; i++)
                        {
                            idx++;
                            ins.Registers.Add(new RegisterParameter(code[idx])
                                {ParameterExpand = FuncParameterExpand.Normal});
                        }
                    }

                    break;
            }

            if (ins != null)
            {
                ins.Offset = index;
            }

            return ins;
        }

        public override string ToString()
        {
            switch (OpCode)
            {
                //0
                case NOP:
                case NF:
                case RET:
                case EXTRY:
                case REGMEMBER:
                case DEBUGGER:
                    return OpCode.ToString().ToLowerInvariant();
                //1
                //%
                case CL:
                case LNOT:
                case TT:
                case TF:
                case SETF:
                case SETNF:
                case INC:
                case DEC:
                case BNOT:
                case TYPEOF:
                case EVAL:
                case EEXP:
                case ASC:
                case CHR:
                case NUM:
                case CHS:
                case INV:
                case CHKINV:
                case INT:
                case REAL:
                case STR:
                case OCTET:
                case SRV:
                case THROW:
                case GLOBAL:
                    return $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}";
                //*
                case JF:
                case JNF:
                case JMP:
                    return $"{OpCode.ToString().ToLowerInvariant()} {((RegisterShort) Registers[0]).Value + Offset:D8}";

                //2
                //instant, %
                case ENTRY:
                    return
                        $"{OpCode.ToString().ToLowerInvariant()} {((RegisterShort) Registers[0]).Value + Offset:D8}, {Registers[1]}";

                //%, *
                case CONST:
                    return $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}, {Registers[1]}";
                case TYPEOFD:
                case TYPEOFI:
                    return $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}.{Registers[1]}";

                //%, %
                case CCL:
                    return
                        $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}-{((RegisterShort) Registers[0]).Value + ((RegisterShort) Registers[1]).Value - 1}";
                //real
                //return
                //    $"{OpCode.ToString().ToLowerInvariant()} {Registers[0].ToString()}, {Registers[1].ToString()}";

                case CP:
                case CEQ:
                case CDEQ:
                case CLT:
                case CGT:
                case INCP:
                case DECP:
                case LOR:
                case LAND:
                case BOR:
                case BXOR:
                case BAND:
                case SAR:
                case SAL:
                case SR:
                case ADD:
                case SUB:
                case MOD:
                case DIV:
                case IDIV:
                case MUL:
                case CHKINS:
                case SETP:
                case GETP:
                case CHGTHIS:
                case ADDCI:
                    return
                        $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}, {Registers[1]}";

                //3
                case LORP:
                case LANDP:
                case BORP:
                case BXORP:
                case BANDP:
                case SARP:
                case SALP:
                case SRP:
                case ADDP:
                case SUBP:
                case MODP:
                case DIVP:
                case IDIVP:
                case MULP:
                    return
                        $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}, {Registers[1]}, {Registers[2]}";
                //x, x.x
                case GPI:
                case GPIS:
                case DELI:
                case INCPI:
                case DECPI:
                case GPD:
                case GPDS:
                case DELD:
                case INCPD:
                case DECPD:
                    return
                        $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}, {Registers[1]}.{Registers[2]}";
                //x.x, x
                case SPI:
                case SPIE:
                case SPIS:
                case SPD:
                case SPDE:
                case SPDEH:
                case SPDS:
                    return
                        $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}.{Registers[1]}, {Registers[2]}";

                //x, x.x, x
                case LORPI:
                case LANDPI:
                case BORPI:
                case BXORPI:
                case BANDPI:
                case SARPI:
                case SALPI:
                case SRPI:
                case ADDPI:
                case SUBPI:
                case MODPI:
                case DIVPI:
                case IDIVPI:
                case MULPI:
                case LORPD:
                case LANDPD:
                case BORPD:
                case BXORPD:
                case BANDPD:
                case SARPD:
                case SALPD:
                case SRPD:
                case ADDPD:
                case SUBPD:
                case MODPD:
                case DIVPD:
                case IDIVPD:
                case MULPD:
                    return
                        $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}, {Registers[1]}.{Registers[2]}, {Registers[3]}";

                //special
                case CALL:
                case NEW:
                case CALLD:
                case CALLI:
                    string param;
                    int pcPos = 2;
                    if (OpCode == CALLD || OpCode == CALLI)
                    {
                        pcPos = 3;
                    }

                    int paramCount = Registers[pcPos].GetSlot();
                    if (paramCount == -1)
                    {
                        param = "...";
                    }
                    else if (paramCount == -2)
                    {
                        param = string.Join(", ", Registers.Skip(pcPos + 1));
                    }
                    else
                    {
                        param = string.Join(", ", Registers.Skip(pcPos + 1));
                    }

                    if (OpCode == CALLD || OpCode == CALLI)
                    {
                        return
                            $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}, {Registers[1]}.{Registers[2]}({param})";
                    }

                    return $"{OpCode.ToString().ToLowerInvariant()} {Registers[0]}, {Registers[1]}({param})";
            }

            return OpCode.ToString().ToLowerInvariant();
        }

        public short[] ToCodes()
        {
            List<short> output = new List<short>(Size) {OpCode.ToS()};
            foreach (var register in Registers)
            {
                switch (register)
                {
                    case RegisterParameter p:
                        output.Add((short) p.ParameterExpand);
                        output.Add((short) p.Slot);
                        break;
                    case RegisterShort s:
                        output.Add(s.Value);
                        break;
                    case RegisterValue v:
                        output.Add((short) v.Slot);
                        break;
                    case RegisterRef r:
                        output.Add((short) r.Slot);
                        break;
                }
            }

            return output.ToArray();
        }
    }
}