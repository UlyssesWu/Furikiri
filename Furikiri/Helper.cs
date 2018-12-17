using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;
using Tjs2.Engine;

namespace Furikiri
{
    internal static class Helper
    {
        public static short ToS(this OpCode code)
        {
            return (short)code;
        }

        public static int RegisterCount(this OpCode code)
        {
            switch (code)
            {
                case OpCode.NOP:
                case OpCode.NF:
                case OpCode.RET:
                case OpCode.EXTRY:
                case OpCode.REGMEMBER:
                case OpCode.DEBUGGER:
                    return 0;

                case OpCode.CL:
                case OpCode.LNOT:
                case OpCode.TT:
                case OpCode.TF:
                case OpCode.SETF:
                case OpCode.SETNF:
                case OpCode.JF:
                case OpCode.JNF:
                case OpCode.JMP:
                case OpCode.INC:
                case OpCode.DEC:
                case OpCode.BNOT:
                case OpCode.TYPEOF:
                case OpCode.EVAL:
                case OpCode.EEXP:
                case OpCode.ASC:
                case OpCode.CHR:
                case OpCode.NUM:
                case OpCode.CHS:
                case OpCode.INV:
                case OpCode.CHKINV:
                case OpCode.INT:
                case OpCode.REAL:
                case OpCode.STR:
                case OpCode.OCTET:
                case OpCode.SRV:
                case OpCode.THROW:
                case OpCode.GLOBAL:
                    return 1;

                case OpCode.CONST:
                case OpCode.CP:
                case OpCode.CCL:
                case OpCode.CEQ:
                case OpCode.CDEQ:
                case OpCode.CLT:
                case OpCode.CGT:
                case OpCode.INCP:
                case OpCode.DECP:
                case OpCode.LOR:
                case OpCode.LAND:
                case OpCode.BOR:
                case OpCode.BXOR:
                case OpCode.BAND:
                case OpCode.SAR:
                case OpCode.SAL:
                case OpCode.SR:
                case OpCode.ADD:
                case OpCode.SUB:
                case OpCode.MOD:
                case OpCode.DIV:
                case OpCode.IDIV:
                case OpCode.MUL:
                case OpCode.CHKINS:
                case OpCode.CALL:
                case OpCode.NEW:
                case OpCode.SETP:
                case OpCode.GETP:
                case OpCode.ENTRY:
                case OpCode.CHGTHIS:
                case OpCode.ADDCI:
                    return 2;

                case OpCode.LORP:
                case OpCode.LANDP:
                case OpCode.BORP:
                case OpCode.BXORP:
                case OpCode.BANDP:
                case OpCode.SARP:
                case OpCode.SALP:
                case OpCode.SRP:
                case OpCode.ADDP:
                case OpCode.SUBP:
                case OpCode.MODP:
                case OpCode.DIVP:
                case OpCode.IDIVP:
                case OpCode.MULP:
                case OpCode.CALLD:
                case OpCode.CALLI:
                case OpCode.GPD:
                case OpCode.SPD:
                case OpCode.SPDE:
                case OpCode.SPDEH:
                case OpCode.GPI:
                case OpCode.SPI:
                case OpCode.SPIE:
                case OpCode.GPDS:
                case OpCode.SPDS:
                case OpCode.GPIS:
                case OpCode.SPIS:
                case OpCode.DELD:
                case OpCode.DELI:
                case OpCode.INCPD:
                case OpCode.INCPI:
                case OpCode.DECPD:
                case OpCode.DECPI:
                case OpCode.TYPEOFD:
                case OpCode.TYPEOFI:
                    return 3;

                case OpCode.LORPD:
                case OpCode.LORPI:
                case OpCode.LANDPD:
                case OpCode.LANDPI:
                case OpCode.BORPD:
                case OpCode.BORPI:
                case OpCode.BXORPD:
                case OpCode.BXORPI:
                case OpCode.BANDPD:
                case OpCode.BANDPI:
                case OpCode.SARPD:
                case OpCode.SARPI:
                case OpCode.SALPD:
                case OpCode.SALPI:
                case OpCode.SRPD:
                case OpCode.SRPI:
                case OpCode.ADDPD:
                case OpCode.ADDPI:
                case OpCode.SUBPD:
                case OpCode.SUBPI:
                case OpCode.MODPD:
                case OpCode.MODPI:
                case OpCode.DIVPD:
                case OpCode.DIVPI:
                case OpCode.IDIVPD:
                case OpCode.IDIVPI:
                case OpCode.MULPD:
                case OpCode.MULPI:
                    return 4;

                case OpCode.LAST:
                case OpCode.PreDec:
                case OpCode.PostInc:
                case OpCode.PostDec:
                case OpCode.Delete:
                case OpCode.FuncCall:
                case OpCode.IgnorePropGet:
                case OpCode.IgnorePropSet:
                default:
                    return 0;
            }
        }

        internal static ITjsVariant ToTjsVariant(this Variant v)
        {
            throw new NotImplementedException();
        }
    }
}
