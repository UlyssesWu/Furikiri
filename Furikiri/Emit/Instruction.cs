using System.Collections.Generic;
using static Furikiri.Emit.OpCode;

namespace Furikiri.Emit
{
    class Instruction
    {
        public OpCode OpCode { get; set; }
        public List<IRegister> Registers { get; set; }
        public int Offset { get; set; }
        public int Size => 1 + Registers.Count;

        public Instruction(OpCode op)
        {
            OpCode = op;
        }

        static Instruction Create(in byte[] code, int index)
        {
            Instruction ins = null;
            var op = (OpCode)code[index];
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
                    { Registers = { new RegisterRef(code[index + 1]) } };
                    break;
                //*
                case JF:
                case JNF:
                case JMP:
                    ins = new Instruction(op)
                    { Registers = { new RegisterValue(code[index + 1]) } };
                    break;
                //2
                //%, %
                case ENTRY:
                    ins = new Instruction(op)
                    { Registers = { new RegisterValue(code[index + 1]), new RegisterRef(code[index + 2]) } };
                    break;
                //%, *
                case CONST:
                case TYPEOFD:
                case TYPEOFI:
                    ins = new Instruction(op)
                    { Registers = { new RegisterRef(code[index + 1]), new RegisterValue(code[index + 2]) } };
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
                    { Registers = { new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]) } };
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
                    ins = new Instruction(op)
                    { Registers = { new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]), new RegisterRef(code[index + 3]) } };
                    break;

                //%, %, *
                case GPD:
                case GPDS:
                case DELD:
                case INCPD:
                case DECPD:
                    ins = new Instruction(op)
                    { Registers = { new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]), new RegisterValue(code[index + 3]) } };
                    break;
                //%, *, %
                case SPD:
                case SPDE:
                case SPDEH:
                case SPDS:
                    ins = new Instruction(op)
                    { Registers = { new RegisterRef(code[index + 1]), new RegisterValue(code[index + 2]), new RegisterRef(code[index + 3]) } };
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
                    { Registers = { new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]), new RegisterRef(code[index + 3]), new RegisterRef(code[index + 4]) } };
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
                    { Registers = { new RegisterRef(code[index + 1]), new RegisterRef(code[index + 2]), new RegisterValue(code[index + 3]), new RegisterRef(code[index + 4]) } };
                    break;

                //special
                case CALL:
                case CALLD:
                case CALLI:
                case NEW:
                    //TODO:
                    break;
            }

            if (ins != null)
            {
                ins.Offset = index;
            }

            return ins;
        }
    }
}
