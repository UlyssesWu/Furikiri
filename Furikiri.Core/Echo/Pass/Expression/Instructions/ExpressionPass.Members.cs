using System;
using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private void ProcessMemberInstruction(
            DecompileContext context,
            Block block,
            Instruction ins,
            Dictionary<int, Expression> ex,
            List<IAstNode> expList)
        {
            switch (ins.OpCode)
            {
                case OpCode.GPD:
                {
                    var dst = ins.GetRegisterSlot(0);
                    var slot = ins.GetRegisterSlot(1);
                    var instance = ex[slot];
                    var name = ins.Data.AsString();
                    var newId = new IdentifierExpression(name) {Instance = instance};
                    ex[dst] = MarkCachedEvaluation(newId, dst, ins);
                    if (ExpressionValueIdentity.TrySnapshotMemberBeforeWrite(
                            context, block, ins, dst, newId, ex, expList)) break;
                    if (ExpressionValueIdentity.TryMaterializeRepeatedMemberReceiver(
                            context, block, ins, dst, newId, ex, expList)) break;
                    if (ShouldEmitStandaloneCall(block, ins, dst))
                    {
                        newId.RequiresStandaloneEvaluation = true;
                        expList.Add(newId);
                    }
                }
                    break;
                case OpCode.GPDS:
                {
                    var dst = ins.GetRegisterSlot(0);
                    var slot = ins.GetRegisterSlot(1);
                    var instance = ex[slot];
                    var name = ins.Data.AsString();
                    var property = new IdentifierExpression(name) {Instance = instance};
                    ex[dst] = new UnaryExpression(property, UnaryOp.PropertyRef);
                    ExpressionValueIdentity.TrySnapshotMemberBeforeWrite(
                        context, block, ins, dst, ex[dst], ex, expList);
                }
                    break;
                case OpCode.GPI:
                {
                    var dst = ins.GetRegisterSlot(0);
                    var obj = ins.GetRegisterSlot(1);
                    var name = ins.GetRegisterSlot(2);

                    PropertyAccessExpression p = new PropertyAccessExpression(ex[name], ex[obj]);
                    ex[dst] = MarkCachedEvaluation(p, dst, ins);
                    if (ExpressionValueIdentity.TrySnapshotMemberBeforeWrite(
                            context, block, ins, dst, p, ex, expList)) break;
                    if (ExpressionValueIdentity.TryMaterializeRepeatedMemberReceiver(
                            context, block, ins, dst, p, ex, expList)) break;
                    if (ShouldEmitStandaloneCall(block, ins, dst))
                    {
                        p.RequiresStandaloneEvaluation = true;
                        expList.Add(p);
                    }
                }
                    break;
                case OpCode.GPIS:
                {
                    var dst = ins.GetRegisterSlot(0);
                    var obj = ins.GetRegisterSlot(1);
                    var name = ins.GetRegisterSlot(2);
                    var access = new PropertyAccessExpression(ex[name], ex[obj]);
                    ex[dst] = new UnaryExpression(access, UnaryOp.PropertyRef);
                    ExpressionValueIdentity.TrySnapshotMemberBeforeWrite(
                        context, block, ins, dst, ex[dst], ex, expList);
                }
                    break;
                case OpCode.SPI:
                case OpCode.SPIE:
                case OpCode.SPIS:
                {
                    var obj = ins.GetRegisterSlot(0);
                    var name = ins.GetRegisterSlot(1);
                    var src = ins.GetRegisterSlot(2);

                    // 匿名集合由 NEW 建立后，编译器常用连续 SPIS 填充元素。
                    // 这里直接并回同一个构造表达式，避免把同一临时对象展开成
                    // `([])[0] = ...; setValue([]);` 这类语义错误的多个新数组。
                    if (ins.OpCode == OpCode.SPIS &&
                        ex[obj] is InvokeExpression collectionCtor &&
                        collectionCtor.InvokeType == InvokeType.Ctor &&
                        IsCollectionCtor(collectionCtor) &&
                        collectionCtor.MethodExpression is IdentifierExpression collectionId)
                    {
                        // 合流块可能从多个前驱请求同一最终状态。同一条 SPIS 对
                        // 同一个构造器只折叠一次；若递归重入产生了新的构造器实例，
                        // 则必须为新实例重新聚合元素，不能留下空字典/数组。
                        if (!_foldedCollectionWrites.Add((ins, collectionCtor)))
                        {
                            break;
                        }

                        _collectionWriteOrigins.TryAdd(
                            collectionCtor, new List<Instruction>());
                        _collectionWriteOrigins[collectionCtor].Add(ins);

                        if (collectionId.FullName == "global.Dictionary")
                        {
                            collectionCtor.Parameters.Add(ex[name]);  // key
                            collectionCtor.Parameters.Add(ex[src]);   // value
                            break;
                        }

                        if (collectionId.FullName == "global.Array" &&
                            ex[name] is ConstantExpression { Variant: TjsInt arrayIndex } &&
                            arrayIndex.IntValue == collectionCtor.Parameters.Count)
                        {
                            collectionCtor.Parameters.Add(ex[src]);
                            break;
                        }
                    }

                    Expression left = new PropertyAccessExpression(ex[name], ex[obj]);
                    if (ins.OpCode == OpCode.SPIS)
                    {
                        left = new UnaryExpression(left, UnaryOp.PropertyRef);
                    }
                    BinaryExpression b = new BinaryExpression(left,
                        ex[src], BinaryOp.Assign);
                    expList.Add(b); //there is no other way to find this expression
                }
                    break;
                //Set
                case OpCode.SPD:
                case OpCode.SPDE:
                case OpCode.SPDEH:
                case OpCode.SPDS:
                {
                    var objSlot = ins.GetRegisterSlot(0);
                    var isClassMemberDeclaration =
                        context.Object.ContextType == TjsContextType.Class &&
                        ins.OpCode == OpCode.SPDS && objSlot == Const.ThisReg;
                    var ident = new IdentifierExpression(ins.Data.AsString())
                        {Instance = isClassMemberDeclaration ? null : ex[objSlot]};
                    Expression left = ident;
                    bool isPropertyRef = ins.OpCode == OpCode.SPDS && !isClassMemberDeclaration &&
                        (context.Object.ContextType != TjsContextType.TopLevel ||
                         objSlot != Const.ThisReg && objSlot != Const.ThisProxyReg);
                    if (isPropertyRef)
                    {
                        left = new UnaryExpression(ident, UnaryOp.PropertyRef);
                    }
                    var right = ex[ins.GetRegisterSlot(2)];
                    BinaryExpression b = new BinaryExpression(left, right, BinaryOp.Assign);
                    if (isClassMemberDeclaration)
                    {
                        b.IsDeclaration = true;
                    }
                    //check declare (SPDS with PropertyRef is never a declaration)
                    if (!isPropertyRef && context.Object.ContextType == TjsContextType.TopLevel &&
                        (objSlot == Const.ThisReg || objSlot == Const.ThisProxyReg))
                    {
                        b.IsDeclaration = true;
                        if (!context.RegisteredMembers.ContainsKey(ident.Name))
                        {
                            var stub = new TjsStub();
                            if (right is ConstantExpression con) //TODO: better type check
                            {
                                stub.Type = con.DataType;
                            }

                            context.RegisteredMembers[ident.Name] = stub;
                        }
                    }

                    expList.Add(b);
                }
                    break;
                case OpCode.SETP:
                {
                    var propertyObjectSlot = ins.GetRegisterSlot(0);
                    var valueSlot = ins.GetRegisterSlot(1);
                    if (ex.TryGetValue(propertyObjectSlot, out var propertyObject) &&
                        ex.TryGetValue(valueSlot, out var value))
                    {
                        // SETP 与 GETP 分别对应属性对象的一元 * 写入和读取。
                        // 属性对象可能先经过 CHGTHIS 改绑上下文，因此这里必须
                        // 保留完整表达式，不能退化成普通成员赋值或直接忽略。
                        var left = new UnaryExpression(
                            propertyObject, UnaryOp.PropertyObject);
                        expList.Add(new BinaryExpression(
                            left, value, BinaryOp.Assign));
                    }
                }
                    break;
                case OpCode.GETP:
                {
                    var dst = ins.GetRegisterSlot(0);
                    var src = ins.GetRegisterSlot(1);
                    if (ex.TryGetValue(src, out var srcExpr))
                    {
                        var read = MarkCachedEvaluation(
                            new UnaryExpression(srcExpr, UnaryOp.PropertyObject), dst, ins);
                        ex[dst] = read;
                        if (ShouldEmitStandaloneCall(block, ins, dst))
                        {
                            // 丢弃读取值也会调用 getter，不能删除该次求值。
                            read.RequiresStandaloneEvaluation = true;
                            expList.Add(read);
                        }
                    }
                }
                    break;
                //Delete
                case OpCode.DELD:
                    DeleteExpression d = new DeleteExpression(ins.Data.AsString());
                    d.Instance = ex[ins.GetRegisterSlot(1)];
                    if (ins.GetRegisterSlot(0) != 0)
                        ex[ins.GetRegisterSlot(0)] = d;
                    if (ShouldEmitStandaloneCall(block, ins, ins.GetRegisterSlot(0)))
                        expList.Add(d);
                    break;
                case OpCode.DELI:
                    DeleteExpression d2 = new DeleteExpression(ex[ins.GetRegisterSlot(2)]);
                    d2.Instance = ex[ins.GetRegisterSlot(1)];
                    if (ins.GetRegisterSlot(0) != 0)
                        ex[ins.GetRegisterSlot(0)] = d2;
                    //Check declare
                    if (d2.Instance is IdentifierExpression toDel)
                    {
                        if (context.RegisteredMembers.ContainsKey(toDel.Name))
                        {
                            context.RegisteredMembers.Remove(toDel.Name);
                        }
                    }

                    if (ShouldEmitStandaloneCall(block, ins, ins.GetRegisterSlot(0)))
                        expList.Add(d2);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ins));
            }
        }
    }
}
