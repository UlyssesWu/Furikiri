using Furikiri.AST.Statements;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    class RegMemberPass : IPass
    {
        //TODO: more robust RegMember detect

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            context.RegisteredMembers.Clear();

            var entry = context.EntryBlock;
            var codes = entry.Instructions;

            // 类代码对象的开头负责建立继承关系并执行 regmember，真正的字段
            // 声明紧随其后。该前导序列不是类体源码，保留会多输出一次父类调用。
            if (context.Object.ContextType == TjsContextType.Class)
            {
                var registerMemberIndex = codes.FindIndex(instruction =>
                    instruction.OpCode == OpCode.REGMEMBER);
                if (registerMemberIndex >= 0)
                {
                    codes.RemoveRange(0, registerMemberIndex + 1);
                }

                return statement;
            }

            int i = 0;
            if (codes.Count < i + 3)
            {
                return statement;
            }

            while (codes.Count >= i + 3)
            {
                if (codes[i].ToString() == "cl %1")
                {
                    codes.RemoveAt(i);
                    break;
                }

                if (codes[i].ToString().StartsWith("const %1,") &&
                    codes[i + 1].ToString() == "chgthis %1, %-1" &&
                    codes[i + 2].ToString().StartsWith("spds %-1.") && codes[i + 2].Registers[2].GetSlot() == 1)
                {
                    var data = codes[i].Data as OperandData;
                    if (data == null)
                    {
                        i += 3;
                        continue;
                    }

                    var func = data.Variant as TjsCodeObject;
                    if (func == null)
                    {
                        i += 3;
                        continue;
                    }

                    var memberData = codes[i + 2].Data as OperandData;
                    if (memberData == null)
                    {
                        i += 3;
                        continue;
                    }

                    var memberName = memberData.Variant as TjsString;
                    if (memberName == null)
                    {
                        i += 3;
                        continue;
                    }

                    context.RegisteredMembers[memberName] = func;
                    i += 3;
                }
                else
                {
                    break;
                }
            }

            codes.RemoveRange(0, i);

            return statement;
        }
    }
}
