using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 维护表达式值的单次求值和对象身份。
    ///
    /// 寄存器传播可以让多个 AST 位置共享同一个节点，但最终输出是树形源码；若不在
    /// 明确的消费点内联或物化，共享节点会被打印多次，进而重复执行构造器和调用。
    /// 本类集中处理这类 def-use 规则，避免把值身份修复散落到各操作码分支中。
    /// </summary>
    internal static class ExpressionValueIdentity
    {
        internal static bool TrySnapshotMemberBeforeWrite(
            DecompileContext context, Block block, Instruction instruction,
            int resultSlot, Expression read,
            IDictionary<int, Expression> expressions, List<IAstNode> statements)
        {
            if (resultSlot <= 0) return false;
            var direct = instruction.OpCode is OpCode.GPD or OpCode.GPDS;
            var pending = new Stack<(Block Block, int Index, bool Changed)>();
            var visited = new HashSet<(Block Block, int Index, bool Changed)>();
            pending.Push((block, block.Instructions.IndexOf(instruction) + 1, false));
            var needsSnapshot = false;
            while (pending.Count > 0)
            {
                var position = pending.Pop();
                if (!visited.Add(position)) continue;
                var current = position.Block;
                if (current.InstructionDatas == null ||
                    current.InstructionDatas.Count != current.Instructions.Count) return false;
                var changed = position.Changed;
                var killed = false;
                for (var index = position.Index; index < current.Instructions.Count; index++)
                {
                    var candidate = current.Instructions[index];
                    var data = current.InstructionDatas[index];
                    if (data.Read.Contains(resultSlot))
                    {
                        // Definitions on only one side of a merge belong to Phi
                        // recovery; do not make their temporary unconditionally visible.
                        if (current != block && current.Dominator?[block.Id] != true) return false;
                        needsSnapshot |= changed;
                    }
                    if (data.Write.Contains(resultSlot)) { killed = true; break; }
                    changed |= candidate.OpCode switch
                    {
                        OpCode.SPD or OpCode.SPDE or OpCode.SPDEH or OpCode.SPDS =>
                            !direct || string.Equals(instruction.Data.AsString(),
                                candidate.Data.AsString(), StringComparison.Ordinal),
                        OpCode.SPI or OpCode.SPIE or OpCode.SPIS => true,
                        _ => false
                    };
                }
                if (!killed)
                    foreach (var successor in current.To) pending.Push((successor, 0, changed));
            }
            if (!needsSnapshot) return false;
            // Member receivers may alias. Preserve the value read before a
            // possible overwrite, including both halves of a member swap.
            var variable = CreateSyntheticVariable(context);
            expressions[resultSlot] = new LocalExpression(variable);
            statements.Add(new BinaryExpression(new LocalExpression(variable), read, BinaryOp.Assign)
            {
                IsDeclaration = true
            });
            return true;
        }

        internal static bool TrySnapshotCopiedLocal(
            DecompileContext context, Block block, Instruction instruction,
            int resultSlot, int sourceSlot, Expression read,
            IDictionary<int, Expression> expressions, List<IAstNode> statements)
        {
            if (resultSlot <= 0 || sourceSlot > Const.ArgBase) return false;
            var pending = new Stack<(Block Block, int Index, bool Changed)>();
            var visited = new HashSet<(Block Block, int Index, bool Changed)>();
            pending.Push((block, block.Instructions.IndexOf(instruction) + 1, false));
            var needsSnapshot = false;
            while (pending.Count > 0 && !needsSnapshot)
            {
                var position = pending.Pop();
                if (!visited.Add(position)) continue;
                var current = position.Block;
                var changed = position.Changed;
                var killed = false;
                for (var index = position.Index; index < current.Instructions.Count; index++)
                {
                    var data = current.InstructionDatas[index];
                    if (changed && data.Read.Contains(resultSlot))
                    {
                        needsSnapshot = true;
                        break;
                    }
                    if (data.Write.Contains(resultSlot)) { killed = true; break; }
                    changed |= data.Write.Contains(sourceSlot);
                }
                if (!killed)
                    foreach (var successor in current.To) pending.Push((successor, 0, changed));
            }
            if (!needsSnapshot) return false;
            // A copied register retains the old value even when its source local is reassigned.
            var variable = CreateSyntheticVariable(context);
            expressions[resultSlot] = new LocalExpression(variable);
            statements.Add(new BinaryExpression(new LocalExpression(variable), read, BinaryOp.Assign)
            {
                IsDeclaration = true
            });
            return true;
        }

        internal static bool TryMaterializeRepeatedMemberReceiver(
            DecompileContext context, Block block, Instruction instruction,
            int resultSlot, Expression read, IDictionary<int, Expression> expressions,
            List<IAstNode> statements)
        {
            if (resultSlot <= 0) return false;
            var pending = new Stack<(Block Block, int Index)>();
            var visited = new HashSet<(Block Block, int Index)>();
            var receivers = new HashSet<Instruction>();
            var crossesBlock = false;
            pending.Push((block, block.Instructions.IndexOf(instruction) + 1));
            while (pending.Count > 0)
            {
                var position = pending.Pop();
                if (!visited.Add(position)) continue;
                var current = position.Block;
                if (current.InstructionDatas == null ||
                    current.InstructionDatas.Count != current.Instructions.Count) return false;
                var overwritten = false;
                for (var index = position.Index; index < current.Instructions.Count; index++)
                {
                    var candidate = current.Instructions[index];
                    var receiverIndex = candidate.OpCode switch
                    {
                        OpCode.GPD or OpCode.GPI or OpCode.GPDS or OpCode.GPIS or
                        OpCode.CALLD or OpCode.CALLI => 1,
                        OpCode.SPD or OpCode.SPDE or OpCode.SPDEH or OpCode.SPDS or
                        OpCode.SPI or OpCode.SPIE or OpCode.SPIS => 0,
                        _ => -1
                    };
                    if (receiverIndex >= 0 && candidate.GetRegisterSlot(receiverIndex) == resultSlot)
                    {
                        // 分支臂产生的值在合流处属于 Phi，不能分别插入声明后再由
                        // 原谓词选择一次；谓词可能是 getter，重复读取会改变分支。
                        if (current != block && (current.Dominator == null ||
                            block.Id >= current.Dominator.Length || !current.Dominator[block.Id]))
                            return false;
                        receivers.Add(candidate);
                        crossesBlock |= current != block;
                    }
                    if (current.InstructionDatas[index].Write.Contains(resultSlot))
                    {
                        overwritten = true;
                        break;
                    }
                }
                if (!overwritten)
                    foreach (var successor in current.To) pending.Push((successor, 0));
            }
            if (receivers.Count < 2 && !crossesBlock) return false;

            // with 控制对象会保留在同一个临时寄存器中供多条成员指令使用。
            // 即使接收者文本相同，也不能重复展开 getter；沿 CFG 只跟踪本次
            // 定义，遇到覆盖即停止，避免把下一轮或另一项临时值的读取误合并。
            // 即使只有一处静态消费，跨块后也可能零次或多次执行，仍须在原定义点读取。
            var variable = CreateSyntheticVariable(context);
            expressions[resultSlot] = new LocalExpression(variable)
            {
                CachedTemporarySlot = (short)resultSlot,
                CachedEvaluationId = instruction.Line
            };
            statements.Add(new BinaryExpression(new LocalExpression(variable), read, BinaryOp.Assign)
            {
                IsDeclaration = true
            });
            return true;
        }

        /// <summary>
        /// 调用或构造结果写入成员后仍跨基本块存活时，先保存到一个局部变量。
        /// 各后继块随后共享局部变量，而不是分别展开同一个有副作用的 AST 节点。
        /// </summary>
        internal static bool TryMaterializeCrossBlockInvocation(
            DecompileContext context, Block block, Instruction instruction,
            int resultSlot, InvokeExpression invocation,
            IDictionary<int, Expression> expressions, List<IAstNode> statements)
        {
            if (context == null || block?.InstructionDatas == null ||
                instruction == null || invocation == null ||
                resultSlot <= Const.ArgBase || resultSlot == Const.ResourceReg ||
                IsCollectionConstructor(invocation) ||
                block.InstructionDatas.Count == 0)
            {
                return false;
            }

            var instructionIndex = block.Instructions.IndexOf(instruction);
            if (instructionIndex < 0 ||
                block.InstructionDatas.Count != block.Instructions.Count)
            {
                return false;
            }

            // 普通分支返回值应继续留给 Phi/条件表达式恢复；若提前生成声明，
            // 它可能被结构化阶段移出原分支而造成调用提前执行。
            if (!IsStoredAsMemberBeforeBlockEnd(
                    block, instructionIndex + 1, resultSlot) ||
                !IsReadBeforeWriteInSuccessors(block, resultSlot))
            {
                return false;
            }

            var variable = CreateSyntheticVariable(context);
            expressions[resultSlot] = new LocalExpression(variable);
            statements.Add(new BinaryExpression(
                new LocalExpression(variable), invocation, BinaryOp.Assign)
            {
                IsDeclaration = true
            });
            return true;
        }

        /// <summary>
        /// 将赋值表达式内联到后续调用的接收者、方法表达式或参数中。
        /// 属性写入指令没有结果寄存器，但 TJS 赋值会返回右值；按节点身份恢复消费点
        /// 才能得到 `(target.item = value).method()`，并保证 value 只求值一次。
        /// </summary>
        internal static void InlineAssignmentsIntoCallConsumers(
            List<IAstNode> statements)
        {
            for (var index = statements.Count - 2; index >= 0; index--)
            {
                if (statements[index] is not BinaryExpression assign ||
                    assign.Op != BinaryOp.Assign)
                {
                    continue;
                }

                // 真正的局部声明不能嵌入表达式；属性赋值即使被标记为声明，
                // 仍然可以作为调用消费的返回值。
                if (assign.IsDeclaration &&
                    assign.Left is LocalExpression or IdentifierExpression { Instance: null })
                {
                    continue;
                }

                var right = assign.Right;
                var inlined = false;
                // 紧邻返回中的成员读取也会消费赋值右值。只替换同一节点身份，
                // 并限制在下一条语句，避免越过副作用或把后续 getter 当成赋值结果。
                if (statements[index + 1] is ReturnExpression returned &&
                    right is InvokeExpression)
                {
                    inlined = TryReplaceExpressionReference(
                        returned.Return, right, assign,
                        value => returned.Return = value, returned);
                }
                // A later call may observe the assigned member. Never delay
                // its write past an intervening statement, even when the RHS
                // shares an expression identity with a subsequent argument.
                if (!inlined)
                {
                    var next = statements[index + 1];
                    if (next is InvokeExpression invoke)
                    {
                        inlined = TryInlineIntoInvoke(invoke, right, assign);
                    }

                    if (!inlined && next is ConditionExpression condition)
                    {
                        var target = condition.Condition;
                        if (target is UnaryExpression { Op: UnaryOp.Not } unary)
                        {
                            target = unary.Target;
                        }

                        if (target is InvokeExpression conditionInvoke)
                        {
                            inlined = TryInlineIntoInvoke(
                                conditionInvoke, right, assign);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 分配不会与真实 VM 局部槽或匿名 `*` 参数占位槽冲突的合成局部变量。
        /// </summary>
        internal static Variable CreateSyntheticVariable(DecompileContext context)
        {
            // 当前尚未访问的真实局部变量可能还没进入 context.Vars，因此必须扫描
            // 整段字节码已经使用的负寄存器范围，而不能按当前字典大小分配。
            var occupiedSlots = context.Vars.Keys
                .Where(slot => slot <= Const.ArgBase && slot != short.MinValue)
                .Concat(context.Blocks
                    .SelectMany(block => block.Instructions)
                    .SelectMany(instruction => instruction.GetRelatedSlots())
                    .Where(slot => slot <= Const.ArgBase &&
                                   slot != short.MinValue))
                .ToList();
            var lowestSlot = occupiedSlots
                .DefaultIfEmpty((short)Const.ArgBase)
                .Min();
            var slot = checked((short)(lowestSlot - 1));
            while (context.Vars.ContainsKey(slot))
            {
                slot = checked((short)(slot - 1));
            }

            var variable = new Variable(slot, context.Object);
            context.Vars[slot] = variable;
            return variable;
        }

        private static bool IsCollectionConstructor(InvokeExpression invocation)
        {
            if (invocation.InvokeType != InvokeType.Ctor ||
                invocation.MethodExpression is not IdentifierExpression identifier)
            {
                return false;
            }

            return identifier.FullName is "global.Array" or "global.Dictionary";
        }

        private static bool IsStoredAsMemberBeforeBlockEnd(
            Block block, int startIndex, int valueSlot)
        {
            for (var index = startIndex; index < block.Instructions.Count; index++)
            {
                var current = block.Instructions[index];
                switch (current.OpCode)
                {
                    case OpCode.SPD:
                    case OpCode.SPDE:
                    case OpCode.SPDEH:
                    case OpCode.SPDS:
                    case OpCode.SPI:
                    case OpCode.SPIE:
                    case OpCode.SPIS:
                        if (current.GetRegisterSlot(2) == valueSlot)
                        {
                            return true;
                        }
                        break;
                    case OpCode.SETP:
                        if (current.GetRegisterSlot(1) == valueSlot)
                        {
                            return true;
                        }
                        break;
                }

                if (block.InstructionDatas[index].Write.Contains(valueSlot))
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 沿 CFG 检查每条路径的首次 use/def。近似活跃集会把后继开头已经覆盖的
        /// 临时槽带回当前块，而这里仅把“先读后写”视为真实的跨块消费。
        /// </summary>
        private static bool IsReadBeforeWriteInSuccessors(Block block, int slot)
        {
            var pending = new Stack<Block>(
                block.To.Where(successor => successor != null));
            var visited = new HashSet<Block>();
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!visited.Add(current))
                {
                    continue;
                }

                var terminated = false;
                foreach (var data in current.InstructionDatas)
                {
                    if (data.Read.Contains(slot))
                    {
                        return true;
                    }

                    if (data.Write.Contains(slot))
                    {
                        terminated = true;
                        break;
                    }
                }

                if (!terminated)
                {
                    foreach (var successor in current.To)
                    {
                        if (successor != null)
                        {
                            pending.Push(successor);
                        }
                    }
                }
            }

            return false;
        }

        private static bool TryInlineIntoInvoke(
            InvokeExpression invoke, Expression right, BinaryExpression assign)
        {
            if (TryReplaceExpressionReference(
                    invoke.Instance, right, assign,
                    replacement => invoke.Instance = replacement, invoke) ||
                TryReplaceExpressionReference(
                    invoke.MethodExpression, right, assign,
                    replacement => invoke.MethodExpression = replacement, invoke))
            {
                return true;
            }

            for (var index = 0; index < invoke.Parameters.Count; index++)
            {
                var parameterIndex = index;
                if (TryReplaceExpressionReference(
                        invoke.Parameters[index], right, assign,
                        replacement => invoke.Parameters[parameterIndex] = replacement,
                        invoke))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 只按引用查找值消费。两个结构相同的表达式可能是两次真实执行，只有共享
        /// AST 节点才代表同一个寄存器值。
        /// </summary>
        private static bool TryReplaceExpressionReference(
            Expression current, Expression expected, Expression replacement,
            Action<Expression> replaceCurrent, IAstNode parent)
        {
            if (current == null)
            {
                return false;
            }

            if (ReferenceEquals(current, expected))
            {
                replaceCurrent(replacement);
                replacement.Parent = parent;
                return true;
            }

            switch (current)
            {
                case BinaryExpression binary:
                    return TryReplaceExpressionReference(
                               binary.Left, expected, replacement,
                               value => binary.Left = value, binary) ||
                           TryReplaceExpressionReference(
                               binary.Right, expected, replacement,
                               value => binary.Right = value, binary);
                case UnaryExpression unary:
                    return TryReplaceExpressionReference(
                        unary.Target, expected, replacement,
                        value => unary.Target = value, unary);
                case IdentifierExpression identifier:
                    return TryReplaceExpressionReference(
                        identifier.Instance, expected, replacement,
                        value => identifier.Instance = value, identifier);
                case PropertyAccessExpression property:
                    return TryReplaceExpressionReference(
                               property.Instance, expected, replacement,
                               value => property.Instance = value, property) ||
                           TryReplaceExpressionReference(
                               property.Property, expected, replacement,
                               value => property.Property = value, property);
                case InvokeExpression nestedInvoke:
                    if (TryReplaceExpressionReference(
                            nestedInvoke.Instance, expected, replacement,
                            value => nestedInvoke.Instance = value, nestedInvoke) ||
                        TryReplaceExpressionReference(
                            nestedInvoke.MethodExpression, expected, replacement,
                            value => nestedInvoke.MethodExpression = value,
                            nestedInvoke))
                    {
                        return true;
                    }

                    for (var index = 0;
                         index < nestedInvoke.Parameters.Count;
                         index++)
                    {
                        var parameterIndex = index;
                        if (TryReplaceExpressionReference(
                                nestedInvoke.Parameters[index], expected,
                                replacement,
                                value => nestedInvoke.Parameters[parameterIndex] =
                                    value,
                                nestedInvoke))
                        {
                            return true;
                        }
                    }

                    return false;
                default:
                    return false;
            }
        }
    }
}
