using System;
using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private void ProcessInstruction(
            DecompileContext context, Block block, Instruction ins,
            Dictionary<int, Expression> ex, List<IAstNode> expList,
            ref Expression flag, ref Expression retExp, int i)
        {
            switch (ins.OpCode)
            {
                case OpCode.CONST:
                case OpCode.CL:
                case OpCode.CCL:
                case OpCode.CP:
                case OpCode.CHGTHIS:
                case OpCode.GLOBAL:
                    ProcessValueInstruction(context, block, ins, ex, expList);
                    break;
                case OpCode.CEQ:
                case OpCode.CDEQ:
                case OpCode.CLT:
                case OpCode.CGT:
                case OpCode.SETF:
                case OpCode.SETNF:
                case OpCode.TT:
                case OpCode.TF:
                case OpCode.NF:
                case OpCode.JF:
                case OpCode.JNF:
                case OpCode.JMP:
                    ProcessConditionInstruction(block, ins, ex, expList, ref flag);
                    break;
                case OpCode.CHS:
                case OpCode.INT:
                case OpCode.REAL:
                case OpCode.STR:
                case OpCode.NUM:
                case OpCode.OCTET:
                case OpCode.ASC:
                case OpCode.CHR:
                case OpCode.LNOT:
                case OpCode.INC:
                case OpCode.DEC:
                case OpCode.BNOT:
                case OpCode.TYPEOF:
                case OpCode.INV:
                case OpCode.CHKINV:
                case OpCode.INCPD:
                case OpCode.DECPD:
                case OpCode.TYPEOFD:
                case OpCode.INCPI:
                case OpCode.DECPI:
                case OpCode.TYPEOFI:
                case OpCode.INCP:
                case OpCode.DECP:
                    ProcessUnaryOperationInstruction(block, ins, ex, expList, i);
                    break;
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
                case OpCode.ADD:
                case OpCode.SUB:
                case OpCode.MOD:
                case OpCode.DIV:
                case OpCode.IDIV:
                case OpCode.MUL:
                case OpCode.BAND:
                case OpCode.BOR:
                case OpCode.BXOR:
                case OpCode.LAND:
                case OpCode.LOR:
                case OpCode.SAR:
                case OpCode.SAL:
                case OpCode.SR:
                case OpCode.CHKINS:
                case OpCode.ADDPD:
                case OpCode.SUBPD:
                case OpCode.MODPD:
                case OpCode.DIVPD:
                case OpCode.IDIVPD:
                case OpCode.MULPD:
                case OpCode.BANDPD:
                case OpCode.BORPD:
                case OpCode.BXORPD:
                case OpCode.LANDPD:
                case OpCode.LORPD:
                case OpCode.SARPD:
                case OpCode.SALPD:
                case OpCode.SRPD:
                case OpCode.ADDPI:
                case OpCode.SUBPI:
                case OpCode.MODPI:
                case OpCode.DIVPI:
                case OpCode.IDIVPI:
                case OpCode.MULPI:
                case OpCode.BANDPI:
                case OpCode.BORPI:
                case OpCode.BXORPI:
                case OpCode.LANDPI:
                case OpCode.LORPI:
                case OpCode.SARPI:
                case OpCode.SALPI:
                case OpCode.SRPI:
                    ProcessBinaryOperationInstruction(context, block, ins, ex, expList);
                    break;
                case OpCode.EVAL:
                case OpCode.EEXP:
                case OpCode.CALL:
                case OpCode.CALLD:
                case OpCode.CALLI:
                case OpCode.NEW:
                    ProcessCallInstruction(context, block, ins, ex, expList);
                    break;
                case OpCode.GPD:
                case OpCode.GPDS:
                case OpCode.GPI:
                case OpCode.GPIS:
                case OpCode.SPI:
                case OpCode.SPIE:
                case OpCode.SPIS:
                case OpCode.SPD:
                case OpCode.SPDE:
                case OpCode.SPDEH:
                case OpCode.SPDS:
                case OpCode.SETP:
                case OpCode.GETP:
                case OpCode.DELD:
                case OpCode.DELI:
                    ProcessMemberInstruction(context, block, ins, ex, expList);
                    break;
                case OpCode.SRV:
                case OpCode.RET:
                case OpCode.ENTRY:
                case OpCode.EXTRY:
                case OpCode.THROW:
                case OpCode.DEBUGGER:
                    ProcessTransferInstruction(ins, ex, expList, ref retExp);
                    break;
                case OpCode.NOP:
                    break;
                case OpCode.ADDCI:
                    // 类名/继承元数据由类声明恢复；不是独立源码表达式。
                    break;
                case OpCode.REGMEMBER:
                    // 成员注册序列已由 RegMemberPass 处理。
                    break;
                case OpCode.LAST:
                    // LAST 是 VM 指令边界；之后的值是编译器内部伪指令，
                    // 不属于 TJS2100 文件中的运行时 opcode。
                    break;
                case OpCode.PreDec:
                    break;
                case OpCode.PostInc:
                    break;
                case OpCode.PostDec:
                    break;
                case OpCode.Delete:
                    break;
                case OpCode.FuncCall:
                    break;
                case OpCode.IgnorePropGet:
                    break;
                case OpCode.IgnorePropSet:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}
