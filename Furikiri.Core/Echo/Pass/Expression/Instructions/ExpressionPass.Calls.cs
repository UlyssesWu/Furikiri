using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        /// <summary>
        /// 调用结果寄存器仍然活跃时，调用会在 return、条件或赋值中被消费，
        /// 不能再额外生成一条独立调用语句，否则会把有副作用的函数执行两次。
        /// </summary>
        private static bool ShouldEmitStandaloneCall(Block block, Instruction instruction, int resultSlot)
        {
            if (resultSlot == Const.ResourceReg)
            {
                return true;
            }

            var instructionIndex = block?.Instructions?.IndexOf(instruction) ?? -1;
            if (instructionIndex < 0 || block.InstructionDatas == null ||
                block.InstructionDatas.Count != block.Instructions.Count)
            {
                return false;
            }

            // 先沿当前基本块检查真实的 use-def 链。调用后紧跟 TT/TF 的短路条件
            // 很常见；若只依赖近似活跃集，可能既输出独立调用，又在条件中再输出一次。
            for (var index = instructionIndex + 1;
                 index < block.InstructionDatas.Count;
                 index++)
            {
                var data = block.InstructionDatas[index];
                if (data.Read.Contains(resultSlot))
                {
                    return false;
                }

                if (data.Write.Contains(resultSlot))
                {
                    return true;
                }
            }

            // 结果流出当前块时仍会被后继消费；仅在块尾确认已经死亡时才作为
            // 丢弃结果的独立调用输出。
            var definingData = block.InstructionDatas[instructionIndex];
            return definingData.LiveOut != null &&
                   !definingData.LiveOut.Contains(resultSlot);
        }

        private static void AddExpandedArgument(InvokeExpression call, RegisterParameter parameter,
            Dictionary<int, Expression> expressions)
        {
            // The operand of an unnamed expansion is padding, not a register.
            if (parameter.ParameterExpand == FuncParameterExpand.FatUnnamedExpand)
            {
                call.Parameters.Add(new IdentifierExpression("*") { Parent = call });
                return;
            }
            var argument = expressions[parameter.GetSlot()];
            if (parameter.ParameterExpand == FuncParameterExpand.FatExpand)
            {
                call.SpreadParameterIndices ??= new HashSet<int>();
                call.SpreadParameterIndices.Add(call.Parameters.Count);
            }
            argument.Parent = call;
            call.Parameters.Add(argument);
        }

        private void ProcessCallInstruction(
            DecompileContext context,
            Block block,
            Instruction ins,
            Dictionary<int, Expression> ex,
            List<IAstNode> expList)
        {
            switch (ins.OpCode)
            {
                case OpCode.EVAL:
                {
                    var srcSlot = ins.GetRegisterSlot(0);
                    if (ex.TryGetValue(srcSlot, out var evalTarget))
                    {
                        // EVAL 会把字符串执行结果写回原寄存器。后续 CHGTHIS、调用
                        // 或赋值必须读取求值后的对象，不能继续使用原字符串表达式。
                        ex[srcSlot] = new UnaryExpression(
                            evalTarget, UnaryOp.Eval);
                    }
                }
                    break;
                case OpCode.EEXP:
                {
                    var srcSlot = ins.GetRegisterSlot(0);
                    if (ex.TryGetValue(srcSlot, out var evalTarget))
                    {
                        // EEXP 丢弃执行结果，只保留一次独立的可观察求值语句。
                        var evalExpr = new UnaryExpression(evalTarget, UnaryOp.Eval);
                        expList.Add(evalExpr);
                    }
                }
                    break;
                //Invoke
                case OpCode.CALL:
                {
                    var method = ex[ins.GetRegisterSlot(1)];
                    var call = new InvokeExpression(method);
                    var dst = ins.GetRegisterSlot(0);
                    call.Instance = null;
                    var paramCount = ins.GetRegisterSlot(2);
                    if (paramCount == -1)
                    {
                        call.HasOmittedArguments = true;
                    }
                    else if (paramCount == -2)
                    {
                        foreach (var reg in ins.Registers.Skip(3).OfType<RegisterParameter>())
                        {
                            AddExpandedArgument(call, reg, ex);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < paramCount; j++)
                        {
                            var pSlot = ins.GetRegisterSlot(3 + j);
                            ex[pSlot].Parent = call;
                            call.Parameters.Add(ex[pSlot]);
                        }
                    }

                    ex[dst] = MarkCachedEvaluation(call, (short)dst, ins);
                    if (!ExpressionValueIdentity.TryMaterializeCrossBlockInvocation(
                            context, block, ins, dst, call, ex, expList) &&
                        ShouldEmitStandaloneCall(block, ins, dst))
                    {
                        expList.Add(call);
                    }
                }
                    break;
                case OpCode.CALLD:
                {
                    var callMethodName = ins.Data.AsString();
                    var call = new InvokeExpression(callMethodName);
                    var dst = ins.GetRegisterSlot(0);
                    var callerSlot = ins.GetRegisterSlot(1);
                    call.Instance = ex[callerSlot];
                    var paramCount = ins.GetRegisterSlot(3);
                    if (paramCount == -1)
                    {
                        call.HasOmittedArguments = true;
                    }
                    else if (paramCount == -2)
                    {
                        foreach (var reg in ins.Registers.Skip(4).OfType<RegisterParameter>())
                        {
                            AddExpandedArgument(call, reg, ex);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < paramCount; j++)
                        {
                            var pSlot = ins.GetRegisterSlot(4 + j);
                            ex[pSlot].Parent = call;
                            call.Parameters.Add(ex[pSlot]);
                        }
                    }

                    ex[dst] = MarkCachedEvaluation(call, (short)dst, ins);
                    if (!ExpressionValueIdentity.TryMaterializeCrossBlockInvocation(
                            context, block, ins, dst, call, ex, expList) &&
                        ShouldEmitStandaloneCall(block, ins, dst))
                    {
                        //Handle RegExp()._compile("//g/[^A-Za-z]")
                        if (callMethodName == Const.RegExpCompile)
                        {
                            if (call.Instance is InvokeExpression invoke && invoke.Method == Const.RegExp)
                            {
                                call.InvokeType = InvokeType.RegExpCompile;
                                ex[callerSlot] = call;
                                break;
                            }
                        }

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
                    call.Instance = ex[callerSlot];
                    var paramCount = ins.GetRegisterSlot(3);
                    if (paramCount == -1)
                    {
                        call.HasOmittedArguments = true;
                    }
                    else if (paramCount == -2)
                    {
                        foreach (var reg in ins.Registers.Skip(4).OfType<RegisterParameter>())
                        {
                            AddExpandedArgument(call, reg, ex);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < paramCount; j++)
                        {
                            var pSlot = ins.GetRegisterSlot(4 + j);
                            ex[pSlot].Parent = call;
                            call.Parameters.Add(ex[pSlot]);
                        }
                    }

                    ex[dst] = MarkCachedEvaluation(call, (short)dst, ins);
                    if (!ExpressionValueIdentity.TryMaterializeCrossBlockInvocation(
                            context, block, ins, dst, call, ex, expList) &&
                        ShouldEmitStandaloneCall(block, ins, dst))
                    {
                        expList.Add(call);
                    }
                }
                    break;
                case OpCode.NEW:
                {
                    InvokeExpression call = new InvokeExpression(ex[ins.GetRegisterSlot(1)]) {InvokeType = InvokeType.Ctor};
                    var dst = ins.GetRegisterSlot(0);
                    call.Instance = null;
                    var paramCount = ins.GetRegisterSlot(2);
                    if (paramCount == -1)
                    {
                        call.HasOmittedArguments = true;
                    }
                    else if (paramCount == -2)
                    {
                        foreach (var reg in ins.Registers.Skip(3).OfType<RegisterParameter>())
                        {
                            AddExpandedArgument(call, reg, ex);
                        }
                    }
                    else
                    {
                        for (int j = 0; j < paramCount; j++)
                        {
                            var pSlot = ins.GetRegisterSlot(3 + j);
                            ex[pSlot].Parent = call;
                            call.Parameters.Add(ex[pSlot]);
                        }
                    }

                    ex[dst] = MarkCachedEvaluation(call, (short)dst, ins);
                    if (IsCollectionCtor(call))
                    {
                        _collectionConstructorOrigins[call] = ins;
                        _collectionWriteOrigins[call] = new List<Instruction>();
                    }
                    if (!ExpressionValueIdentity.TryMaterializeCrossBlockInvocation(
                            context, block, ins, dst, call, ex, expList) &&
                        ShouldEmitStandaloneCall(block, ins, dst))
                    {
                        expList.Add(call);
                    }
                }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ins));
            }
        }
    }
}
