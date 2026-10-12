using System;
using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private void ProcessUnaryOperationInstruction(
            Block block,
            Instruction ins,
            Dictionary<int, Expression> ex,
            List<IAstNode> expList,
            int i)
        {
            switch (ins.OpCode)
            {
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
                        case OpCode.BNOT:
                            op = UnaryOp.BitNot;
                            break;
                        case OpCode.OCTET:
                            op = UnaryOp.ToByteArray;
                            break;
                        case OpCode.ASC:
                            // ASC 对应 TJS2 的 # 运算符：取得字符串首字符的编码。
                            op = UnaryOp.ToCharacterCode;
                            break;
                        case OpCode.CHR:
                            // CHR 对应 TJS2 的 $ 运算符：由字符编码生成字符。
                            op = UnaryOp.FromCharacterCode;
                            break;
                        case OpCode.LNOT:
                            op = UnaryOp.Not;
                            break;
                        case OpCode.TYPEOF:
                            op = UnaryOp.TypeOf;
                            break;
                        case OpCode.INV:
                            op = UnaryOp.Invalidate;
                            break;
                        case OpCode.CHKINV:
                            op = UnaryOp.IsValid;
                            break;
                    }

                    // INC/DEC on VM temp registers often model numeric index stepping.
                    // If the target is a concrete integer constant, fold it directly to
                    // avoid producing invalid chains like 0++++ in decompiled output.
                    if ((op == UnaryOp.Inc || op == UnaryOp.Dec) &&
                        dst is ConstantExpression constExpr &&
                        constExpr.Variant is TjsInt intVal)
                    {
                        var nextValue = op == UnaryOp.Inc ? intVal.IntValue + 1 : intVal.IntValue - 1;
                        var folded = new ConstantExpression(new TjsInt(nextValue));
                        ex[dstSlot] = folded;
                        break;
                    }

                    // 后置自增/自减会先用 CP 把旧值保存到临时寄存器，再修改局部变量。
                    // 若把两条指令分别写成 `++i` 和随后使用 `i`，不仅形式错误，传给
                    // 函数/索引的值也会提前一位。把临时快照改写成 `i++/i--`，并由该
                    // 表达式承担唯一一次副作用。
                    if ((op == UnaryOp.Inc || op == UnaryOp.Dec) &&
                        dstSlot <= Const.ArgBase && i > 0 &&
                        block.Instructions[i - 1].OpCode == OpCode.CP)
                    {
                        var copy = block.Instructions[i - 1];
                        var snapshotSlot = copy.GetRegisterSlot(0);
                        var sourceSlot = copy.GetRegisterSlot(1);
                        if (snapshotSlot > Const.ArgBase && sourceSlot == dstSlot)
                        {
                            ex[snapshotSlot] = new UnaryExpression(dst, op) {IsPrefix = false};
                            ex[dstSlot] = dst;
                            break;
                        }
                    }

                    var u = new UnaryExpression(dst, op);
                    if (dstSlot > Const.ArgBase)
                    {
                        // 一元运算结果保存在 VM 临时槽时记录同一次求值身份。
                        // switch(typeof value) 后续会反复比较该槽，但源码中的
                        // typeof 只执行一次，不能在每个 case 前重新展开。
                        u.CachedTemporarySlot = (short)dstSlot;
                        u.CachedEvaluationId = ins.Line;
                    }
                    if (dstSlot > 0)
                    {
                        ex[dstSlot] = u;
                    }
                    // INV (invalidate) 是必须作为语句输出的副作用操作，
                    // 无论目标寄存器是参数槽还是临时寄存器，都应加入 expList。
                    if (dstSlot <= Const.ArgBase || op == UnaryOp.Invalidate)
                    {
                        expList.Add(u);
                    }
                }
                    break;
                case OpCode.INCPD:
                case OpCode.DECPD:
                case OpCode.TYPEOFD:
                {
                    var res = ins.GetRegisterSlot(0);
                    var obj = ins.GetRegisterSlot(1);
                    var name = ins.Data.AsString();
                    var op = UnaryOp.Unknown;
                    switch (ins.OpCode)
                    {
                        case OpCode.INCPD:
                            op = UnaryOp.Inc;
                            break;
                        case OpCode.DECPD:
                            op = UnaryOp.Dec;
                            break;
                        case OpCode.TYPEOFD:
                            op = UnaryOp.TypeOf;
                            break;
                    }

                    //var u = new UnaryExpression(new IdentifierExpression(name), op) {Instance = ex[obj]};
                    var u = new UnaryExpression(new IdentifierExpression(name) {Instance = ex[obj]}, op);
                    if (res > Const.ArgBase)
                    {
                        u.CachedTemporarySlot = (short)res;
                        u.CachedEvaluationId = ins.Line;
                    }
                    // 后置成员自增会先用 GPD 读取旧值，再以 INCPD/DECPD 丢弃新值。
                    // 两条指令必须合成 `object.member++` 并写回旧值快照；若分别输出
                    // `object.member++` 和随后读取 `object.member`，调用实参会提前一位。
                    if (op is UnaryOp.Inc or UnaryOp.Dec && res == 0 && i > 0 &&
                        block.Instructions[i - 1].OpCode == OpCode.GPD)
                    {
                        var snapshot = block.Instructions[i - 1];
                        var snapshotSlot = snapshot.GetRegisterSlot(0);
                        var snapshotObject = snapshot.GetRegisterSlot(1);
                        if (snapshotSlot > Const.ArgBase && snapshotObject == obj &&
                            string.Equals(snapshot.Data.AsString(), name,
                                StringComparison.Ordinal))
                        {
                            ex[snapshotSlot] = new UnaryExpression(
                                new IdentifierExpression(name) { Instance = ex[obj] }, op)
                            {
                                IsPrefix = false
                            };
                            break;
                        }
                    }

                    if (res != 0) //copy to %res
                    {
                        u.IsPrefix = true; // INCPD/DECPD 存储操作后的新值，语义上为前置运算符
                        ex[res] = u;
                    }

                    // 只有当结果被丢弃（res==0）时才作为独立语句输出；
                    // 若 res!=0，结果将被下游指令（如 CEQ）内联使用，
                    // 不应再作为单独语句输出以避免重复执行。
                    if (res == 0)
                    {
                        expList.Add(u);
                    }
                }
                    break;
                case OpCode.INCPI:
                case OpCode.DECPI:
                case OpCode.TYPEOFI:
                {
                    var res = ins.GetRegisterSlot(0);
                    var obj = ins.GetRegisterSlot(1);
                    var name = ins.GetRegisterSlot(2);
                    var op = UnaryOp.Unknown;
                    switch (ins.OpCode)
                    {
                        case OpCode.INCPI:
                            op = UnaryOp.Inc;
                            break;
                        case OpCode.DECPI:
                            op = UnaryOp.Dec;
                            break;
                        case OpCode.TYPEOFI:
                            op = UnaryOp.TypeOf;
                            break;
                    }

                    var u = new UnaryExpression(new PropertyAccessExpression(ex[name], ex[obj]), op);
                    if (res > Const.ArgBase)
                    {
                        u.CachedTemporarySlot = (short)res;
                        u.CachedEvaluationId = ins.Line;
                    }
                    // 动态成员的后置自增使用 GPI + INCPI/DECPI 保存旧值，处理原则
                    // 与直接成员一致。对象槽和键槽都必须相同，避免跨表达式合并。
                    if (op is UnaryOp.Inc or UnaryOp.Dec && res == 0 && i > 0 &&
                        block.Instructions[i - 1].OpCode == OpCode.GPI)
                    {
                        var snapshot = block.Instructions[i - 1];
                        var snapshotSlot = snapshot.GetRegisterSlot(0);
                        if (snapshotSlot > Const.ArgBase &&
                            snapshot.GetRegisterSlot(1) == obj &&
                            snapshot.GetRegisterSlot(2) == name)
                        {
                            ex[snapshotSlot] = new UnaryExpression(
                                new PropertyAccessExpression(ex[name], ex[obj]), op)
                            {
                                IsPrefix = false
                            };
                            break;
                        }
                    }

                    if (res != 0) //copy to %res
                    {
                        u.IsPrefix = true; // INCPI/DECPI 存储操作后的新值，语义上为前置运算符
                        ex[res] = u;
                    }

                    if (res == 0)
                    {
                        expList.Add(u);
                    }
                }
                    break;
                case OpCode.INCP:
                case OpCode.DECP:
                {
                    var res = ins.GetRegisterSlot(0);
                    var propertySlot = ins.GetRegisterSlot(1);
                    var target = new UnaryExpression(ex[propertySlot], UnaryOp.PropertyObject);
                    var op = ins.OpCode == OpCode.INCP ? UnaryOp.Inc : UnaryOp.Dec;
                    var update = new UnaryExpression(target, op) { IsPrefix = res != 0 };

                    // 后置形式先 GETP 保存旧值，再以无结果 INCP/DECP 更新属性。
                    // 合并到旧值快照中，保留 getter/setter 次数和传出的旧值。
                    if (res == 0 && i > 0 &&
                        block.Instructions[i - 1].OpCode == OpCode.GETP)
                    {
                        var snapshot = block.Instructions[i - 1];
                        if (snapshot.GetRegisterSlot(1) == propertySlot &&
                            snapshot.GetRegisterSlot(0) > Const.ArgBase)
                        {
                            ex[snapshot.GetRegisterSlot(0)] = update;
                            break;
                        }
                    }

                    if (res != 0) ex[res] = MarkCachedEvaluation(update, res, ins);
                    if (ShouldEmitStandaloneCall(block, ins, res)) expList.Add(update);
                }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ins));
            }
        }
    }
}
