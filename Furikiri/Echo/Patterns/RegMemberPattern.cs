using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// only appears on beginning
    /// <example>this.a = [object]</example>
    /// </summary>
    class RegMemberPattern : ITjsPattern
    {
        public static RegMemberPattern TryMatch(List<Instruction> codes, int index, DecompileContext context)
        {
            if (codes.Count < index + 3)
            {
                return null;
            }

            RegMemberPattern pattern = null;
            while (codes.Count >= index + 3)
            {
                if (codes[index].ToString() == "cl %1")
                {
                    if (pattern != null)
                    {
                        pattern.HasCL = true;
                    }
                    return pattern;
                }
                if (codes[index].ToString().StartsWith("const %1,") &&
                    codes[index + 1].ToString() == "chgthis %1, %-1" &&
                    codes[index + 2].ToString().StartsWith("spds %-1.") && codes[index + 2].Registers[2].GetSlot() == 1)
                {
                    var data = codes[index].Data as OperandData;
                    if (data == null)
                    {
                        index += 3;
                        continue;
                    }

                    var func = data.Variant as TjsCodeObject;
                    if (func == null)
                    {
                        index += 3;
                        continue;
                    }

                    var memberData = codes[index + 2].Data as OperandData;
                    if (memberData == null)
                    {
                        index += 3;
                        continue;
                    }

                    var memberName = memberData.Variant as TjsString;
                    if (memberName == null)
                    {
                        index += 3;
                        continue;
                    }

                    if (pattern == null)
                    {
                        pattern = new RegMemberPattern();
                    }
                    pattern.Members[memberName] = func;
                    index += 3;
                }
                else
                {
                    return pattern;
                }
            }

            return pattern;
        }

        public bool HasCL { get; set; } = false;
        public int Length => 3 * Members.Count + (HasCL ? 1 : 0);

        public Dictionary<string, TjsCodeObject> Members { get; set; } = new Dictionary<string, TjsCodeObject>();

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var m in Members)
            {
                sb.AppendLine($"this.{m.Key} = {m.Value};");
            }

            return sb.ToString();
        }
    }
}
