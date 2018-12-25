using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// only appears on beginning
    /// <example>this.a = [object]</example>
    /// </summary>
    class RegMemberPattern : IPattern
    {
        public static RegMemberPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            if (codes.Count < i + 3)
            {
                return null;
            }

            RegMemberPattern pattern = null;
            while (codes.Count >= i + 3)
            {
                if (codes[i].ToString() == "cl %1")
                {
                    if (pattern != null)
                    {
                        pattern.HasCL = true;
                    }
                    return pattern;
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

                    if (pattern == null)
                    {
                        pattern = new RegMemberPattern();
                    }
                    pattern.Members[memberName] = func;
                    i += 3;
                }
                else
                {
                    return pattern;
                }
            }

            return pattern;
        }

        public bool HasCL { get; set; } = false;
        public bool Terminal => true;
        public int Length => 3 * Members.Count + (HasCL ? 1 : 0);

        public Dictionary<string, TjsCodeObject> Members { get; set; } = new Dictionary<string, TjsCodeObject>();

        public override string ToString()
        {
            return string.Join(Environment.NewLine, Members.Select(kv => $"this.{kv.Key} = {kv.Value};"));
        }
    }
}
