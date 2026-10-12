using System;
using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private void ProcessBinaryOperationInstruction(
            DecompileContext context,
            Block block,
            Instruction ins,
            Dictionary<int, Expression> ex,
            List<IAstNode> expList)
        {
            switch (ins.OpCode)
            {
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
                {
                    var res = ins.GetRegisterSlot(0);
                    var target = new UnaryExpression(
                        ex[ins.GetRegisterSlot(1)], UnaryOp.PropertyObject);
                    var op = ins.OpCode switch
                    {
                        OpCode.LORP => BinaryOp.LogicOr,
                        OpCode.LANDP => BinaryOp.LogicAnd,
                        OpCode.BORP => BinaryOp.BitOr,
                        OpCode.BXORP => BinaryOp.BitXor,
                        OpCode.BANDP => BinaryOp.BitAnd,
                        OpCode.SARP => BinaryOp.NumberShiftRight,
                        OpCode.SALP => BinaryOp.NumberShiftLeft,
                        OpCode.SRP => BinaryOp.BitShiftRight,
                        OpCode.ADDP => BinaryOp.Add,
                        OpCode.SUBP => BinaryOp.Sub,
                        OpCode.MODP => BinaryOp.Mod,
                        OpCode.DIVP => BinaryOp.Div,
                        OpCode.IDIVP => BinaryOp.Idiv,
                        OpCode.MULP => BinaryOp.Mul,
                        _ => throw new ArgumentOutOfRangeException()
                    };
                    var update = new BinaryExpression(target, ex[ins.GetRegisterSlot(2)], op)
                    {
                        IsSelfAssignment = true
                    };
                    if (res != 0) ex[res] = MarkCachedEvaluation(update, res, ins);
                    if (ShouldEmitStandaloneCall(block, ins, res)) expList.Add(update);
                }
                    break;
                //Binary Operation
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
                {
                    var dstSlot = ins.GetRegisterSlot(0);
                    var srcSlot = ins.GetRegisterSlot(1);
                    var saveToEx = true; //Set to Expression
                    var appendToExpList = dstSlot <= Const.ArgBase;
                    var declare = false; //Is declaration

                    Expression dst = null;
                    if (ex.ContainsKey(dstSlot))
                    {
                        dst = ex[dstSlot];
                    }
                    else if (dstSlot <= Const.ArgBase)
                    {
                        var l = GetLocalExpression(context, dstSlot);
                        //if (!l.IsParameter)
                        //{
                        //    expList.Add(l);
                        //}
                        dst = l;
                        ex[dstSlot] = l;
                        saveToEx = false;
                        declare = true;
                    }

                    Expression src;
                    if (ex.ContainsKey(srcSlot))
                    {
                        src = ex[srcSlot];
                    }
                    else
                    {
                        src = GetLocalExpression(context, srcSlot);
                    }

                    var op = BinaryOp.Unknown;
                    switch (ins.OpCode)
                    {
                        case OpCode.ADD:
                            op = BinaryOp.Add;
                            break;
                        case OpCode.SUB:
                            op = BinaryOp.Sub;
                            break;
                        case OpCode.MOD:
                            op = BinaryOp.Mod;
                            break;
                        case OpCode.DIV:
                            op = BinaryOp.Div;
                            break;
                        case OpCode.IDIV:
                            op = BinaryOp.Idiv;
                            break;
                        case OpCode.MUL:
                            op = BinaryOp.Mul;
                            break;
                        case OpCode.BAND:
                            op = BinaryOp.BitAnd;
                            break;
                        case OpCode.BOR:
                            op = BinaryOp.BitOr;
                            break;
                        case OpCode.BXOR:
                            op = BinaryOp.BitXor;
                            break;
                        case OpCode.LAND:
                            op = BinaryOp.LogicAnd;
                            break;
                        case OpCode.LOR:
                            op = BinaryOp.LogicOr;
                            break;
                        case OpCode.SAR:
                            op = BinaryOp.NumberShiftRight;
                            break;
                        case OpCode.SAL:
                            op = BinaryOp.NumberShiftLeft;
                            break;
                        case OpCode.SR:
                            op = BinaryOp.BitShiftRight;
                            break;
                        //case OpCode.CP: //moved!
                        //    op = BinaryOp.Assign;
                        //    push = true;
                        //break;
                        case OpCode.CHKINS:
                            op = BinaryOp.InstanceOf;
                            break;
                    }

                    BinaryExpression b = new BinaryExpression(dst, src, op)
                    {
                        IsDeclaration = declare,
                        // ADD/BOR 等寄存器指令会原地改写目标寄存器。临时寄存器
                        // 仍可作为普通表达式传播；参数和命名局部则必须保留成
                        // `x += y` / `x |= y` 一类副作用语句。
                        IsSelfAssignment = appendToExpList
                    };

                    MarkCachedEvaluation(b, (short)dstSlot, ins);

                    if (saveToEx)
                    {
                        ex[dstSlot] = b;
                    }

                    if (appendToExpList)
                    {
                        expList.Add(b);

                        // 参数/局部寄存器上的运算已经作为副作用语句输出（如 p3 += x）。
                        // 后续读取必须仍是 p3，而不能把整棵赋值表达式再次内联，否则在
                        // 分支合流时会生成带重复副作用的三元表达式。
                        ex[dstSlot] = GetLocalExpression(context, (short)dstSlot);
                    }
                }
                    break;
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
                {
                    var res = ins.GetRegisterSlot(0);
                    var obj = ins.GetRegisterSlot(1);
                    var name = ins.Data.AsString();
                    var op = BinaryOp.Unknown;

                    var src = ex[ins.GetRegisterSlot(3)];
                    switch (ins.OpCode)
                    {
                        case OpCode.ADDPD:
                            op = BinaryOp.Add;
                            break;
                        case OpCode.SUBPD:
                            op = BinaryOp.Sub;
                            break;
                        case OpCode.MODPD:
                            op = BinaryOp.Mod;
                            break;
                        case OpCode.DIVPD:
                            op = BinaryOp.Div;
                            break;
                        case OpCode.IDIVPD:
                            op = BinaryOp.Idiv;
                            break;
                        case OpCode.MULPD:
                            op = BinaryOp.Mul;
                            break;
                        case OpCode.BANDPD:
                            op = BinaryOp.BitAnd;
                            break;
                        case OpCode.BORPD:
                            op = BinaryOp.BitOr;
                            break;
                        case OpCode.BXORPD:
                            op = BinaryOp.BitXor;
                            break;
                        case OpCode.LANDPD:
                            op = BinaryOp.LogicAnd;
                            break;
                        case OpCode.LORPD:
                            op = BinaryOp.LogicOr;
                            break;
                        case OpCode.SARPD:
                            op = BinaryOp.NumberShiftRight;
                            break;
                        case OpCode.SALPD:
                            op = BinaryOp.NumberShiftLeft;
                            break;
                        case OpCode.SRPD:
                            op = BinaryOp.BitShiftRight;
                            break;
                    }

                    BinaryExpression b = new BinaryExpression(new IdentifierExpression(name) {Instance = ex[obj]},
                        src, op)
                    {
                        // *PD 指令会把运算结果写回对象属性，语义是 obj.name op= src。
                        IsSelfAssignment = true
                    };

                    if (res != 0)
                    {
                        ex[res] = b;
                    }

                    expList.Add(b);
                }
                    break;
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
                {
                    var res = ins.GetRegisterSlot(0);
                    var obj = ins.GetRegisterSlot(1);
                    var name = ins.GetRegisterSlot(2);
                    var op = BinaryOp.Unknown;

                    var src = ex[ins.GetRegisterSlot(3)];
                    switch (ins.OpCode)
                    {
                        case OpCode.ADDPI:
                            op = BinaryOp.Add;
                            break;
                        case OpCode.SUBPI:
                            op = BinaryOp.Sub;
                            break;
                        case OpCode.MODPI:
                            op = BinaryOp.Mod;
                            break;
                        case OpCode.DIVPI:
                            op = BinaryOp.Div;
                            break;
                        case OpCode.IDIVPI:
                            op = BinaryOp.Idiv;
                            break;
                        case OpCode.MULPI:
                            op = BinaryOp.Mul;
                            break;
                        case OpCode.BANDPI:
                            op = BinaryOp.BitAnd;
                            break;
                        case OpCode.BORPI:
                            op = BinaryOp.BitOr;
                            break;
                        case OpCode.BXORPI:
                            op = BinaryOp.BitXor;
                            break;
                        case OpCode.LANDPI:
                            op = BinaryOp.LogicAnd;
                            break;
                        case OpCode.LORPI:
                            op = BinaryOp.LogicOr;
                            break;
                        case OpCode.SARPI:
                            op = BinaryOp.NumberShiftRight;
                            break;
                        case OpCode.SALPI:
                            op = BinaryOp.NumberShiftLeft;
                            break;
                        case OpCode.SRPI:
                            op = BinaryOp.BitShiftRight;
                            break;
                    }

                    BinaryExpression b =
                        new BinaryExpression(new PropertyAccessExpression(ex[name], ex[obj]), src, op)
                        {
                            // *PI 与 *PD 相同，只是属性名由寄存器动态给出。
                            IsSelfAssignment = true
                        };

                    if (res != 0)
                    {
                        ex[res] = b;
                    }

                    expList.Add(b);
                }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ins));
            }
        }
    }
}
