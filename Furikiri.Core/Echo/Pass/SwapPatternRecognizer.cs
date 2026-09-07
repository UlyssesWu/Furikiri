using System.Collections.Generic;
using System.Linq;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 交换右操作数的寻址方式。
    /// </summary>
    internal enum SwapOperandKind
    {
        Register,
        DirectProperty,
        IndirectProperty,
    }

    /// <summary>
    /// 编译器展开交换运算后得到的稳定字节码形状。
    /// </summary>
    internal sealed class SwapPattern
    {
        public SwapOperandKind LeftKind { get; init; } = SwapOperandKind.Register;
        public SwapOperandKind RightKind { get; init; }
        public int InstructionCount { get; init; }
        public short LeftSlot { get; init; }
        public short RightValueSlot { get; init; }
        public short TemporarySlot { get; init; }
        public short LeftObjectSlot { get; init; }
        public short LeftMemberSlot { get; init; }
        public IReadOnlyList<string> LeftMemberPath { get; init; }
        public short ObjectSlot { get; init; }
        public short MemberSlot { get; init; }
        public IReadOnlyList<string> MemberPath { get; init; }
    }

    /// <summary>
    /// 识别 TJS2 编译器将 <c>left &lt;-&gt; right</c> 展开后的复制序列。
    /// </summary>
    /// <remarks>
    /// 识别必须同时验证临时寄存器在序列之后不再读取。只看连续指令形状会把
    /// 用户手写的普通赋值误合并为交换，从而删除仍被后续代码使用的旧值。
    /// </remarks>
    internal static class SwapPatternRecognizer
    {
        public static bool TryMatch(Block block, int index, out SwapPattern pattern)
        {
            return TryMatchFlatPropertyPair(block, index, out pattern) ||
                   TryMatchFlatPropertyToRegister(block, index, out pattern) ||
                   TryMatchNestedIndexedPropertyToRegister(block, index, out pattern) ||
                   TryMatchRegister(block, index, out pattern) ||
                   TryMatchDirectProperty(block, index, out pattern) ||
                   TryMatchIndirectProperty(block, index, out pattern);
        }

        /// <summary>
        /// 识别 <c>object.member &lt;-&gt; local</c> 及其动态索引镜像形态。
        /// </summary>
        private static bool TryMatchFlatPropertyToRegister(
            Block block, int index, out SwapPattern pattern)
        {
            pattern = null;
            if (!HasInstructions(block, index, 3) ||
                !TryReadFlatProperty(block.Instructions[index], out var left))
            {
                return false;
            }

            var writeLeft = block.Instructions[index + 1];
            var writeRight = block.Instructions[index + 2];
            if (writeRight.OpCode != OpCode.CP)
            {
                return false;
            }

            var rightSlot = writeRight.GetRegisterSlot(0);
            if (left.ObjectSlot >= Const.ResourceReg ||
                rightSlot > Const.ArgBase ||
                writeRight.GetRegisterSlot(1) != left.ValueSlot ||
                !WriteMatchesFlatProperty(writeLeft, left, rightSlot) ||
                !TemporaryDiesAfter(block, index + 3, left.ValueSlot))
            {
                return false;
            }

            pattern = new SwapPattern
            {
                LeftKind = left.Kind,
                RightKind = SwapOperandKind.Register,
                InstructionCount = 3,
                TemporarySlot = left.ValueSlot,
                RightValueSlot = rightSlot,
                LeftObjectSlot = left.ObjectSlot,
                LeftMemberSlot = left.MemberSlot,
                LeftMemberPath = left.MemberPath,
            };
            return true;
        }

        /// <summary>
        /// 动态属性的接收者本身是成员时，编译器会在写回前重新读取接收者。
        /// 此处同时核对两次读取的基槽和成员名，避免把不同对象误认成交换。
        /// </summary>
        private static bool TryMatchNestedIndexedPropertyToRegister(
            Block block, int index, out SwapPattern pattern)
        {
            pattern = null;
            if (!HasInstructions(block, index, 5))
            {
                return false;
            }

            var readObject = block.Instructions[index];
            var readLeft = block.Instructions[index + 1];
            var replayObject = block.Instructions[index + 2];
            var writeLeft = block.Instructions[index + 3];
            var writeRight = block.Instructions[index + 4];
            if (readObject.OpCode != OpCode.GPD || readLeft.OpCode != OpCode.GPI ||
                replayObject.OpCode != OpCode.GPD ||
                writeLeft.OpCode is not (OpCode.SPI or OpCode.SPIE) ||
                writeRight.OpCode != OpCode.CP)
            {
                return false;
            }

            var baseSlot = readObject.GetRegisterSlot(1);
            var firstObjectSlot = readObject.GetRegisterSlot(0);
            var secondObjectSlot = replayObject.GetRegisterSlot(0);
            var oldValueSlot = readLeft.GetRegisterSlot(0);
            var memberSlot = readLeft.GetRegisterSlot(2);
            var rightSlot = writeRight.GetRegisterSlot(0);
            if (baseSlot >= Const.ResourceReg ||
                readLeft.GetRegisterSlot(1) != firstObjectSlot ||
                replayObject.GetRegisterSlot(1) != baseSlot ||
                replayObject.Data.AsString() != readObject.Data.AsString() ||
                writeLeft.GetRegisterSlot(0) != secondObjectSlot ||
                writeLeft.GetRegisterSlot(1) != memberSlot ||
                writeLeft.GetRegisterSlot(2) != rightSlot ||
                rightSlot > Const.ArgBase ||
                writeRight.GetRegisterSlot(1) != oldValueSlot ||
                !TemporaryDiesAfter(block, index + 5, oldValueSlot) ||
                !TemporaryDiesAfter(block, index + 5, firstObjectSlot) ||
                !TemporaryDiesAfter(block, index + 5, secondObjectSlot))
            {
                return false;
            }

            pattern = new SwapPattern
            {
                LeftKind = SwapOperandKind.IndirectProperty,
                RightKind = SwapOperandKind.Register,
                InstructionCount = 5,
                TemporarySlot = oldValueSlot,
                RightValueSlot = rightSlot,
                LeftObjectSlot = baseSlot,
                LeftMemberSlot = memberSlot,
                LeftMemberPath = new[] { readObject.Data.AsString() },
            };
            return true;
        }

        /// <summary>
        /// 识别两个已求值属性之间的交换：先各读取一次旧值，再交叉写回。
        /// </summary>
        /// <remarks>
        /// 当前只接收单层直接/间接属性，且接收者必须是 this、参数或局部变量。
        /// 这样不会把缓存于临时寄存器中的带副作用接收者重新展开并重复求值。
        /// </remarks>
        private static bool TryMatchFlatPropertyPair(
            Block block, int index, out SwapPattern pattern)
        {
            pattern = null;
            if (!HasInstructions(block, index, 4) ||
                !TryReadFlatProperty(block.Instructions[index], out var left) ||
                !TryReadFlatProperty(block.Instructions[index + 1], out var right) ||
                left.ValueSlot == right.ValueSlot ||
                left.ObjectSlot >= Const.ResourceReg ||
                right.ObjectSlot >= Const.ResourceReg ||
                !WriteMatchesFlatProperty(block.Instructions[index + 2], left,
                    right.ValueSlot) ||
                !WriteMatchesFlatProperty(block.Instructions[index + 3], right,
                    left.ValueSlot) ||
                !TemporaryDiesAfter(block, index + 4, left.ValueSlot) ||
                !TemporaryDiesAfter(block, index + 4, right.ValueSlot))
            {
                return false;
            }

            pattern = new SwapPattern
            {
                LeftKind = left.Kind,
                RightKind = right.Kind,
                InstructionCount = 4,
                TemporarySlot = left.ValueSlot,
                RightValueSlot = right.ValueSlot,
                LeftObjectSlot = left.ObjectSlot,
                LeftMemberSlot = left.MemberSlot,
                LeftMemberPath = left.MemberPath,
                ObjectSlot = right.ObjectSlot,
                MemberSlot = right.MemberSlot,
                MemberPath = right.MemberPath,
            };
            return true;
        }

        private static bool TryReadFlatProperty(
            Instruction instruction, out FlatPropertyOperand operand)
        {
            operand = null;
            if (instruction.OpCode == OpCode.GPD)
            {
                operand = new FlatPropertyOperand
                {
                    Kind = SwapOperandKind.DirectProperty,
                    ValueSlot = instruction.GetRegisterSlot(0),
                    ObjectSlot = instruction.GetRegisterSlot(1),
                    MemberPath = new[] { instruction.Data.AsString() },
                };
            }
            else if (instruction.OpCode == OpCode.GPI)
            {
                operand = new FlatPropertyOperand
                {
                    Kind = SwapOperandKind.IndirectProperty,
                    ValueSlot = instruction.GetRegisterSlot(0),
                    ObjectSlot = instruction.GetRegisterSlot(1),
                    MemberSlot = instruction.GetRegisterSlot(2),
                };
            }

            return operand != null && operand.ValueSlot > Const.ArgBase;
        }

        private static bool WriteMatchesFlatProperty(
            Instruction instruction, FlatPropertyOperand operand, short valueSlot)
        {
            if (operand.Kind == SwapOperandKind.DirectProperty)
            {
                return instruction.OpCode is OpCode.SPD or OpCode.SPDE or OpCode.SPDEH &&
                       instruction.GetRegisterSlot(0) == operand.ObjectSlot &&
                       instruction.GetRegisterSlot(2) == valueSlot &&
                       instruction.Data.AsString() == operand.MemberPath[0];
            }

            return instruction.OpCode is OpCode.SPI or OpCode.SPIE &&
                   instruction.GetRegisterSlot(0) == operand.ObjectSlot &&
                   instruction.GetRegisterSlot(1) == operand.MemberSlot &&
                   instruction.GetRegisterSlot(2) == valueSlot;
        }

        private sealed class FlatPropertyOperand
        {
            public SwapOperandKind Kind { get; init; }
            public short ValueSlot { get; init; }
            public short ObjectSlot { get; init; }
            public short MemberSlot { get; init; }
            public IReadOnlyList<string> MemberPath { get; init; }
        }

        private static bool TryMatchRegister(
            Block block, int index, out SwapPattern pattern)
        {
            pattern = null;
            if (!HasInstructions(block, index, 3))
            {
                return false;
            }

            var save = block.Instructions[index];
            var writeLeft = block.Instructions[index + 1];
            var writeRight = block.Instructions[index + 2];
            if (save.OpCode != OpCode.CP || writeLeft.OpCode != OpCode.CP ||
                writeRight.OpCode != OpCode.CP)
            {
                return false;
            }

            var temporarySlot = save.GetRegisterSlot(0);
            var leftSlot = save.GetRegisterSlot(1);
            var rightSlot = writeLeft.GetRegisterSlot(1);
            if (temporarySlot <= Const.ArgBase ||
                leftSlot > Const.ArgBase || rightSlot > Const.ArgBase ||
                leftSlot == rightSlot ||
                writeLeft.GetRegisterSlot(0) != leftSlot ||
                writeRight.GetRegisterSlot(0) != rightSlot ||
                writeRight.GetRegisterSlot(1) != temporarySlot ||
                !TemporaryDiesAfter(block, index + 3, temporarySlot))
            {
                return false;
            }

            pattern = new SwapPattern
            {
                RightKind = SwapOperandKind.Register,
                InstructionCount = 3,
                LeftSlot = leftSlot,
                RightValueSlot = rightSlot,
                TemporarySlot = temporarySlot,
            };
            return true;
        }

        private static bool TryMatchDirectProperty(
            Block block, int index, out SwapPattern pattern)
        {
            pattern = null;
            if (!HasInstructions(block, index, 4))
            {
                return false;
            }

            var save = block.Instructions[index];
            if (save.OpCode != OpCode.CP ||
                block.Instructions[index + 1].OpCode != OpCode.GPD)
            {
                return false;
            }

            var temporarySlot = save.GetRegisterSlot(0);
            var leftSlot = save.GetRegisterSlot(1);
            if (temporarySlot <= Const.ArgBase || leftSlot > Const.ArgBase)
            {
                return false;
            }

            // 先收集从同一基槽连续读取的成员路径。最后一次读取的结果必须
            // 紧接着写入左操作数，否则它只是普通的属性读取序列。
            var cursor = index + 1;
            var firstRead = block.Instructions[cursor];
            var objectSlot = firstRead.GetRegisterSlot(1);
            var memberPath = new List<string>();
            short rightValueSlot = 0;
            while (cursor < block.Instructions.Count)
            {
                var read = block.Instructions[cursor];
                if (read.OpCode != OpCode.GPD ||
                    memberPath.Count > 0 && read.GetRegisterSlot(1) != rightValueSlot)
                {
                    break;
                }

                rightValueSlot = read.GetRegisterSlot(0);
                if (rightValueSlot <= Const.ArgBase)
                {
                    return false;
                }
                memberPath.Add(read.Data.AsString());
                cursor++;
            }

            if (memberPath.Count == 0 || cursor >= block.Instructions.Count)
            {
                return false;
            }

            var writeLeft = block.Instructions[cursor];
            if (writeLeft.OpCode != OpCode.CP ||
                writeLeft.GetRegisterSlot(0) != leftSlot ||
                writeLeft.GetRegisterSlot(1) != rightValueSlot)
            {
                return false;
            }
            cursor++;

            // 写回时编译器会重新计算末级属性之前的接收者。逐段核对路径，
            // 防止把两个名字相同但实例不同的成员访问误认成交换。
            var writeObjectSlot = objectSlot;
            for (var pathIndex = 0; pathIndex < memberPath.Count - 1; pathIndex++)
            {
                if (cursor >= block.Instructions.Count)
                {
                    return false;
                }

                var replay = block.Instructions[cursor];
                if (replay.OpCode != OpCode.GPD ||
                    replay.GetRegisterSlot(1) != writeObjectSlot ||
                    replay.Data.AsString() != memberPath[pathIndex])
                {
                    return false;
                }

                writeObjectSlot = replay.GetRegisterSlot(0);
                cursor++;
            }

            if (cursor >= block.Instructions.Count)
            {
                return false;
            }

            var writeRight = block.Instructions[cursor];
            if (writeRight.OpCode is not (OpCode.SPD or OpCode.SPDE or OpCode.SPDEH) ||
                writeRight.GetRegisterSlot(0) != writeObjectSlot ||
                writeRight.GetRegisterSlot(2) != temporarySlot ||
                writeRight.Data.AsString() != memberPath[^1] ||
                !TemporaryDiesAfter(block, cursor + 1, temporarySlot))
            {
                return false;
            }

            pattern = new SwapPattern
            {
                RightKind = SwapOperandKind.DirectProperty,
                InstructionCount = cursor - index + 1,
                LeftSlot = leftSlot,
                RightValueSlot = rightValueSlot,
                TemporarySlot = temporarySlot,
                ObjectSlot = objectSlot,
                MemberPath = memberPath,
            };
            return true;
        }

        private static bool TryMatchIndirectProperty(
            Block block, int index, out SwapPattern pattern)
        {
            pattern = null;
            if (!HasInstructions(block, index, 4))
            {
                return false;
            }

            var save = block.Instructions[index];
            var readRight = block.Instructions[index + 1];
            var writeLeft = block.Instructions[index + 2];
            var writeRight = block.Instructions[index + 3];
            if (save.OpCode != OpCode.CP || readRight.OpCode != OpCode.GPI ||
                writeLeft.OpCode != OpCode.CP ||
                writeRight.OpCode is not (OpCode.SPI or OpCode.SPIE))
            {
                return false;
            }

            var temporarySlot = save.GetRegisterSlot(0);
            var leftSlot = save.GetRegisterSlot(1);
            var rightValueSlot = readRight.GetRegisterSlot(0);
            var objectSlot = readRight.GetRegisterSlot(1);
            var memberSlot = readRight.GetRegisterSlot(2);
            if (temporarySlot <= Const.ArgBase || leftSlot > Const.ArgBase ||
                rightValueSlot <= Const.ArgBase ||
                writeLeft.GetRegisterSlot(0) != leftSlot ||
                writeLeft.GetRegisterSlot(1) != rightValueSlot ||
                writeRight.GetRegisterSlot(0) != objectSlot ||
                writeRight.GetRegisterSlot(1) != memberSlot ||
                writeRight.GetRegisterSlot(2) != temporarySlot ||
                !TemporaryDiesAfter(block, index + 4, temporarySlot))
            {
                return false;
            }

            pattern = new SwapPattern
            {
                RightKind = SwapOperandKind.IndirectProperty,
                InstructionCount = 4,
                LeftSlot = leftSlot,
                RightValueSlot = rightValueSlot,
                TemporarySlot = temporarySlot,
                ObjectSlot = objectSlot,
                MemberSlot = memberSlot,
            };
            return true;
        }

        private static bool HasInstructions(Block block, int index, int count) =>
            block?.Instructions != null && block.InstructionDatas != null &&
            index >= 0 && index + count <= block.Instructions.Count &&
            index + count <= block.InstructionDatas.Count;

        private static bool TemporaryDiesAfter(Block block, int index, int slot) =>
            IsRegisterOverwrittenOrDeadBeforeRead(
                block, index, slot, new HashSet<(Block Block, int Index)>());

        /// <summary>
        /// 沿每条后继路径检查：临时槽在下一次读取之前必须被覆盖，或者路径结束。
        /// 遇到无覆盖的环时保守拒绝匹配。
        /// </summary>
        private static bool IsRegisterOverwrittenOrDeadBeforeRead(
            Block block, int instructionIndex, int slot,
            HashSet<(Block Block, int Index)> visiting)
        {
            if (block == null || block.InstructionDatas == null ||
                !visiting.Add((block, instructionIndex)))
            {
                return false;
            }

            for (var i = instructionIndex; i < block.InstructionDatas.Count; i++)
            {
                var data = block.InstructionDatas[i];
                if (data.Read?.Contains(slot) == true)
                {
                    visiting.Remove((block, instructionIndex));
                    return false;
                }
                if (data.Write?.Contains(slot) == true)
                {
                    visiting.Remove((block, instructionIndex));
                    return true;
                }
            }

            var result = block.To.Count == 0 || block.To.All(successor =>
                IsRegisterOverwrittenOrDeadBeforeRead(successor, 0, slot, visiting));
            visiting.Remove((block, instructionIndex));
            return result;
        }
    }
}
