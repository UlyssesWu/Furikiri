using System;
using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    class ExpressionPass : IPass
    {
        public static IdentifierExpression Global = new IdentifierExpression("global", IdentifierType.Global);
        public static IdentifierExpression This = new IdentifierExpression("this", IdentifierType.This);
        public static IdentifierExpression ThisProxy = new IdentifierExpression("", IdentifierType.ThisProxy);

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            var entry = context.EntryBlock;
            var exps = new Dictionary<int, Expression>()
            {
                {-1, This},
                {-2, ThisProxy},
            };
            BlockProcess(context, entry, exps);
            return statement;
        }

        public void BlockProcess(DecompileContext context, Block block,
            Dictionary<int, Expression> exps) //TODO: If we need flag expression?
        {
            if (block.Statements != null)
            {
                return;
            }

            Expression flagExp = null;
            var ex = new Dictionary<int, Expression>(exps);
            var expList = new List<IAstNode>();
            block.Statements = expList;
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var ins = block.Instructions[i];
                switch (ins.OpCode)
                {
                    case OpCode.NOP:
                        break;
                    case OpCode.CONST:
                    {
                        var data = (OperandData) ins.Data;
                        var constExp = new ConstantExpression(data.Variant);
                        ex[ins.GetRegisterSlot(0)] = constExp;
                    }
                        break;
                    case OpCode.CL:
                    {
                        ex[ins.GetRegisterSlot(0)] = null;
                    }
                        break;
                    case OpCode.CCL:
                        break;
                    case OpCode.CEQ:
                    case OpCode.CDEQ:
                    case OpCode.CLT:
                    case OpCode.CGT:
                    {
                        var left = ex[ins.GetRegisterSlot(0)];
                        var right = ex[ins.GetRegisterSlot(1)];
                        BinaryOp op = BinaryOp.Unknown;
                        switch (ins.OpCode)
                        {
                            case OpCode.CEQ:
                                op = BinaryOp.Equal;
                                break;
                            case OpCode.CDEQ:
                                op = BinaryOp.Congruent;
                                break;
                            case OpCode.CLT:
                                op = BinaryOp.LessThan;
                                break;
                            case OpCode.CGT:
                                op = BinaryOp.GreaterThan;
                                break;
                        }

                        var b = new BinaryExpression(left, right, op);
                        flagExp = b;
                    }
                        break;
                    case OpCode.SETF:
                        break;
                    case OpCode.SETNF:
                        break;
                    case OpCode.NF:
                    {
                        flagExp = new UnaryExpression(flagExp, UnaryOp.Not);
                    }
                        break;
                    case OpCode.JF:
                    case OpCode.JNF:
                    {
                        bool flag = ins.OpCode == OpCode.JF;
                        expList.Add(new ConditionExpression(flagExp, flag) {JumpTo = ((JumpData) ins.Data).Goto.Line});
                    }
                        break;
                    case OpCode.JMP:
                    {
                        expList.Add(new GotoExpression());
                    }
                        break;
                    case OpCode.INC:
                    case OpCode.DEC:
                    case OpCode.CHS:
                    case OpCode.INT:
                    case OpCode.REAL:
                    case OpCode.STR:
                    case OpCode.NUM:
                    case OpCode.OCTET:
                    case OpCode.TT:
                    case OpCode.TF:
                    case OpCode.LNOT:
                    {
                        var dstSlot = ins.GetRegisterSlot(0);
                        var dst = ex[dstSlot];
                        var op = UnaryOp.Unknown;
                        switch (ins.OpCode)
                        {
                            case OpCode.INC:
                                op = UnaryOp.Inc;
                                break;
                            case OpCode.DEC:
                                op = UnaryOp.Dec;
                                break;
                            case OpCode.CHS:
                                op = UnaryOp.InvertSign;
                                break;
                            case OpCode.INT:
                                op = UnaryOp.ToInt;
                                break;
                            case OpCode.REAL:
                                op = UnaryOp.ToReal;
                                break;
                            case OpCode.STR:
                                op = UnaryOp.ToString;
                                break;
                            case OpCode.NUM:
                                op = UnaryOp.ToNumber;
                                break;
                            case OpCode.OCTET:
                                op = UnaryOp.ToByteArray;
                                break;
                            case OpCode.TT:
                                op = UnaryOp.IsTrue;
                                break;
                            case OpCode.TF:
                                op = UnaryOp.IsFalse;
                                break;
                            case OpCode.LNOT:
                                op = UnaryOp.Not;
                                break;
                        }

                        var u = new UnaryExpression(dst, op);
                        ex[dstSlot] = u;
                    }
                        break;
                    case OpCode.INCPD:
                        break;
                    case OpCode.INCPI:
                        break;
                    case OpCode.INCP:
                        break;
                    case OpCode.DECPD:
                        break;
                    case OpCode.DECPI:
                        break;
                    case OpCode.DECP:
                        break;
                    case OpCode.LORPD:
                        break;
                    case OpCode.LORPI:
                        break;
                    case OpCode.LORP:
                        break;
                    case OpCode.LANDPD:
                        break;
                    case OpCode.LANDPI:
                        break;
                    case OpCode.LANDP:
                        break;
                    case OpCode.BORPD:
                        break;
                    case OpCode.BORPI:
                        break;
                    case OpCode.BORP:
                        break;
                    case OpCode.BXOR:
                        break;
                    case OpCode.BXORPD:
                        break;
                    case OpCode.BXORPI:
                        break;
                    case OpCode.BXORP:
                        break;
                    case OpCode.BANDPD:
                        break;
                    case OpCode.BANDPI:
                        break;
                    case OpCode.BANDP:
                        break;
                    case OpCode.SARPD:
                        break;
                    case OpCode.SARPI:
                        break;
                    case OpCode.SARP:
                        break;
                    case OpCode.SALPD:
                        break;
                    case OpCode.SALPI:
                        break;
                    case OpCode.SALP:
                        break;
                    case OpCode.SRPD:
                        break;
                    case OpCode.SRPI:
                        break;
                    case OpCode.SRP:
                        break;
                    case OpCode.ADD:
                    case OpCode.SUB:
                    case OpCode.MOD:
                    case OpCode.DIV:
                    case OpCode.IDIV:
                    case OpCode.MUL:
                    case OpCode.BAND:
                    case OpCode.BOR:
                    case OpCode.LAND:
                    case OpCode.LOR:
                    case OpCode.SAR:
                    case OpCode.SAL:
                    case OpCode.SR:
                    case OpCode.CP:
                    case OpCode.CHKINS:
                    {
                        var dstSlot = ins.GetRegisterSlot(0);
                        Expression dst = null;
                        if (ex.ContainsKey(dstSlot))
                        {
                            dst = ex[dstSlot];
                        }
                        else if (dstSlot < -2)
                        {
                            var l = new LocalExpression(context.Object, dstSlot);
                            //if (!l.IsParameter)
                            //{
                            //    expList.Add(l);
                            //}
                            dst = l;
                        }

                        var src = ex[ins.GetRegisterSlot(1)];
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
                            case OpCode.CP:
                                op = BinaryOp.Assign;
                                break;
                            case OpCode.CHKINS:
                                op = BinaryOp.InstanceOf;
                                break;
                        }

                        BinaryExpression b = new BinaryExpression(dst, src, op);
                        ex[dstSlot] = b;
                    }
                        break;
                    case OpCode.ADDPD:
                        break;
                    case OpCode.ADDPI:
                        break;
                    case OpCode.ADDP:
                        break;
                    case OpCode.SUBPD:
                        break;
                    case OpCode.SUBPI:
                        break;
                    case OpCode.SUBP:
                        break;
                    case OpCode.MODPD:
                        break;
                    case OpCode.MODPI:
                        break;
                    case OpCode.MODP:
                        break;
                    case OpCode.DIVPD:
                        break;
                    case OpCode.DIVPI:
                        break;
                    case OpCode.DIVP:
                        break;
                    case OpCode.IDIVPD:
                        break;
                    case OpCode.IDIVPI:
                        break;
                    case OpCode.IDIVP:
                        break;
                    case OpCode.MULPD:
                        break;
                    case OpCode.MULPI:
                        break;
                    case OpCode.MULP:
                        break;
                    case OpCode.BNOT:
                        break;
                    case OpCode.TYPEOF:
                        break;
                    case OpCode.TYPEOFD:
                        break;
                    case OpCode.TYPEOFI:
                        break;
                    case OpCode.EVAL:
                        break;
                    case OpCode.EEXP:
                        break;
                    case OpCode.ASC:
                        break;
                    case OpCode.CHR:
                        break;
                    case OpCode.INV:
                        break;
                    case OpCode.CHKINV:
                        break;
                    case OpCode.CALL:
                    {
                        var call = new InvokeExpression(((OperandData) ins.Data).Variant as TjsCodeObject);
                        var dst = ins.GetRegisterSlot(0);
                        call.Caller = null;
                        var paramCount = ins.GetRegisterSlot(2);
                        if (paramCount == -1)
                        {
                            //...
                            //do nothing
                        }
                        else
                        {
                            for (int j = 0; j < paramCount; j++)
                            {
                                var pSlot = ins.GetRegisterSlot(3 + j);
                                call.Parameters.Add(ex[pSlot]);
                            }
                        }

                        ex[dst] = call;
                        if (dst == 0) //just execute and discard result
                        {
                            expList.Add(call);
                        }
                    }
                        break;
                    case OpCode.CALLD:
                    {
                        var call = new InvokeExpression(ins.Data.AsString());
                        var dst = ins.GetRegisterSlot(0);
                        var callerSlot = ins.GetRegisterSlot(1);
                        call.Caller = ex[callerSlot];
                        var paramCount = ins.GetRegisterSlot(3);
                        if (paramCount == -1)
                        {
                            //...
                            //do nothing
                        }
                        else
                        {
                            for (int j = 0; j < paramCount; j++)
                            {
                                var pSlot = ins.GetRegisterSlot(4 + j);
                                call.Parameters.Add(ex[pSlot]);
                            }
                        }

                        ex[dst] = call;
                        if (dst == 0) //just execute and discard result
                        {
                            expList.Add(call);
                        }
                    }
                        break;
                    case OpCode.CALLI:
                    {
                        //InvokeExpression call = null;
                        //var operand = ((OperandData) ins.Data).Variant;
                        //if (operand is TjsString str)
                        //{
                        //    call = new InvokeExpression(str.StringValue);
                        //}
                        //else
                        //{
                        //    call = new InvokeExpression(operand as TjsCodeObject);
                        //}
                        InvokeExpression call = new InvokeExpression(ex[ins.GetRegisterSlot(2)]);
                        var dst = ins.GetRegisterSlot(0);
                        var callerSlot = ins.GetRegisterSlot(1);
                        call.Caller = ex[callerSlot];
                        var paramCount = ins.GetRegisterSlot(3);
                        if (paramCount == -1)
                        {
                            //...
                            //do nothing
                        }
                        else
                        {
                            for (int j = 0; j < paramCount; j++)
                            {
                                var pSlot = ins.GetRegisterSlot(4 + j);
                                call.Parameters.Add(ex[pSlot]);
                            }
                        }

                        ex[dst] = call;
                        if (dst == 0) //just execute and discard result
                        {
                            expList.Add(call);
                        }
                    }
                        break;
                    case OpCode.NEW:
                    {
                        InvokeExpression call = new InvokeExpression(ex[ins.GetRegisterSlot(1)]);
                        var dst = ins.GetRegisterSlot(0);
                        call.Caller = null;
                        var paramCount = ins.GetRegisterSlot(2);
                        if (paramCount == -1)
                        {
                            //...
                            //do nothing
                        }
                        else
                        {
                            for (int j = 0; j < paramCount; j++)
                            {
                                var pSlot = ins.GetRegisterSlot(3 + j);
                                call.Parameters.Add(ex[pSlot]);
                            }
                        }

                        ex[dst] = call;
                        if (dst == 0) //just execute and discard result
                        {
                            expList.Add(call);
                        }
                    }
                        break;
                    case OpCode.GPD:
                    case OpCode.GPDS:
                    {
                        var dst = ins.GetRegisterSlot(0);
                        var slot = ins.GetRegisterSlot(1);
                        var id = ex[slot] as IdentifierExpression;
                        var name = ins.Data.AsString();
                        var newId = new IdentifierExpression(name);
                        newId.Parent = id;
                        id.Child = newId;
                        ex[dst] = newId;
                    }
                        break;
                    case OpCode.GPI:
                        break;
                    case OpCode.SPI:
                        break;
                    case OpCode.SPIE:
                        break;
                    case OpCode.SPD:
                    case OpCode.SPDE:
                    case OpCode.SPDEH:
                    case OpCode.SPDS:
                    {
                        var left = new IdentifierExpression(ins.Data.AsString()) {Parent = ex[ins.GetRegisterSlot(0)]};
                        var right = ex[ins.GetRegisterSlot(2)];
                        BinaryExpression b = new BinaryExpression(left, right, BinaryOp.Assign);
                        expList.Add(b);
                    }
                        break;
                    case OpCode.GPIS:
                        break;
                    case OpCode.SPIS:
                        break;
                    case OpCode.SETP:
                        break;
                    case OpCode.GETP:
                        break;
                    case OpCode.DELD:
                        break;
                    case OpCode.DELI:
                        break;
                    case OpCode.SRV:
                        break;
                    case OpCode.RET:
                    {
                        expList.Add(new ReturnExpression());
                    }
                        break;
                    case OpCode.ENTRY:
                        break;
                    case OpCode.EXTRY:
                        break;
                    case OpCode.THROW:
                        break;
                    case OpCode.CHGTHIS:
                        break;
                    case OpCode.GLOBAL:
                    {
                        ex[ins.GetRegisterSlot(0)] = Global;
                    }
                        break;
                    case OpCode.ADDCI:
                        break;
                    case OpCode.REGMEMBER:
                        break;
                    case OpCode.DEBUGGER:
                        break;
                    case OpCode.LAST:
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

            foreach (var succ in block.To)
            {
                BlockProcess(context, succ, ex);
            }
        }
    }
}