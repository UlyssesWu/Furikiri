using System;
using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private void ProcessValueInstruction(
            DecompileContext context,
            Block block,
            Instruction ins,
            Dictionary<int, Expression> ex,
            List<IAstNode> expList)
        {
            switch (ins.OpCode)
            {
                case OpCode.CONST:
                {
                    var data = (OperandData) ins.Data;
                    var constExp = new ConstantExpression(data.Variant);
                    ex[ins.GetRegisterSlot(0)] = constExp;
                }
                    break;
                case OpCode.CL:
                {
                    var slot = ins.GetRegisterSlot(0);
                    if (slot <= Const.ArgBase)
                    {
                        var variable = context.Vars.TryGetValue(slot, out var knownVariable)
                            ? knownVariable
                            : new Variable(slot, context.Object);
                        context.Vars.TryAdd(slot, variable);
                        var local = new LocalExpression(variable);
                        ex[slot] = local;
                        if (!variable.IsParameter)
                        {
                            expList.Add(new BinaryExpression(local, new ConstantExpression(TjsVoid.Void), BinaryOp.Assign)
                            {
                                IsDeclaration = true
                            });
                        }
                    }
                    else
                    {
                        ex[slot] = new ConstantExpression(TjsVoid.Void);
                    }
                }
                    break;
                case OpCode.CCL:
                    for (int j = ins.GetRegisterSlot(0); j < ins.GetRegisterSlot(1); j++)
                    {
                        if (j <= Const.ArgBase)
                        {
                            var slot = (short)j;
                            var variable = context.Vars.TryGetValue(slot, out var knownVariable)
                                ? knownVariable
                                : new Variable(slot, context.Object);
                            context.Vars.TryAdd(slot, variable);
                            var local = new LocalExpression(variable);
                            ex[slot] = local;
                            if (!variable.IsParameter)
                            {
                                expList.Add(new BinaryExpression(local, new ConstantExpression(TjsVoid.Void), BinaryOp.Assign)
                                {
                                    IsDeclaration = true
                                });
                            }
                        }
                        else
                        {
                            ex[j] = new ConstantExpression(TjsVoid.Void);
                        }
                    }
                    break;
                case OpCode.CP:
                {
                    var dstSlot = ins.GetRegisterSlot(0);
                    var srcSlot = ins.GetRegisterSlot(1);

                    Expression src;
                    if (ex.ContainsKey(srcSlot))
                    {
                        src = ex[srcSlot];
                    }
                    else
                    {
                        src = GetLocalExpression(context, srcSlot);
                    }

                    if (ExpressionValueIdentity.TrySnapshotCopiedLocal(
                            context, block, ins, dstSlot, srcSlot, src, ex, expList))
                    {
                        break;
                    }

                    Expression dst = null;
                    if (dstSlot <= Const.ArgBase)
                    {
                        var declare = !context.IsArg(dstSlot);
                        var l = GetLocalExpression(context, dstSlot);

                        //This is wrong. todo: phi
                        //if ((src is LocalExpression localExp && localExp.VariableDef.Slot == dstSlot) || (src is BinaryExpression binaryExp && binaryExp.Left is LocalExpression le && le.Slot == dstSlot))
                        //{
                        //    ex[dstSlot] = l;
                        //    continue;
                        //}

                        //if (!l.IsParameter)
                        //{
                        //    expList.Add(l);
                        //}
                        dst = l;
                        ex[dstSlot] = l; //assignment -> statements, local -> expressions

                        // 当局部变量从已知属性/方法赋值时，使用 name_0、name_1 命名。
                        if (declare && l.VariableDef.Name == null && src is IdentifierExpression srcId && srcId.Instance != null && !string.IsNullOrEmpty(srcId.Name))
                        {
                            var baseName = srcId.Name;
                            l.VariableDef.Name = GetUniqueDerivedLocalName(baseName, context, ex, expList);
                        }

                        BinaryExpression b = new BinaryExpression(dst, src, BinaryOp.Assign) {IsDeclaration = declare };
                        var insertedDeclarationAtArrayInit = false;
                        // Keep temp collection constructor aliases stable so subsequent
                        // element assignments print against the local variable rather than
                        // repeatedly materializing anonymous literals (e.g. [][0] = ...).
                        if (srcSlot > Const.ArgBase && IsCollectionCtor(src))
                        {
                            ex[srcSlot] = l;
                            var firstAssignIndex = -1;
                            for (var k = 0; k < expList.Count; k++)
                            {
                                if (expList[k] is BinaryExpression prevAssign &&
                                    prevAssign.Left is PropertyAccessExpression access &&
                                    ReferenceEquals(access.Instance, src))
                                {
                                    if (firstAssignIndex < 0)
                                    {
                                        firstAssignIndex = k;
                                    }
                                    access.Instance = l;
                                }
                            }

                            if (firstAssignIndex >= 0)
                            {
                                expList.Insert(firstAssignIndex, b);
                                insertedDeclarationAtArrayInit = true;
                            }
                        }
                        else if (srcSlot > Const.ArgBase)
                        {
                            // 当临时寄存器被赋给命名局部变量后，将该临时寄存器重定向到局部变量。
                            // 这样后续指令（如 CDEQ）对同一临时寄存器的引用会变成对命名局部变量的引用，
                            // 而不是再次展开原始表达式（如重复函数调用）。
                            ex[srcSlot] = l;
                        }

                        if (!insertedDeclarationAtArrayInit)
                        {
                            //ex[dstSlot] = b;
                            expList.Add(b);
                        }
                    }
                    else if (ex.ContainsKey(dstSlot))
                    {
                        ex[dstSlot] = SnapshotCopiedEvaluation(
                            src, (short)dstSlot, ins);
                    }
                    else if (dstSlot != 0)
                    {
                        ex[dstSlot] = SnapshotCopiedEvaluation(
                            src, (short)dstSlot, ins);
                    }
                }
                    break;
                case OpCode.CHGTHIS:
                {
                    var ico = new BinaryExpression(ex[ins.GetRegisterSlot(0)], ex[ins.GetRegisterSlot(1)], BinaryOp.InContextOf);
                    ex[ins.GetRegisterSlot(0)] = ico;
                }
                    break;
                case OpCode.GLOBAL:
                {
                    ex[ins.GetRegisterSlot(0)] = Global;
                }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ins));
            }
        }
    }
}
