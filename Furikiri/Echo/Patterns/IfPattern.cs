using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// If
    /// </summary>
    class IfPattern : IBranchPattern
    {
        public bool Terminal { get; set; }
        public int Length { get; }
        public List<IPattern> If { get; set; }
        public IExpressionPattern Condition { get; set; }
        public List<IPattern> Else { get; set; }

        public static IfPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            return null;
            if (codes[i].OpCode == OpCode.TT)
            {
                var exp = context.Expressions[codes[i].GetRegisterSlot(0)];
                if (i + 1 < codes.Count && codes[i + 1].OpCode.IsJump(true)) //if
                {
                    if (exp != null)
                    {
                        exp.Terminal = false;
                    }
                }
            }

            return null;
        }

        public BranchType BranchType { get; } = BranchType.If;

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"if ({Condition})");
            sb.AppendLine("{");
            foreach (var pattern in If)
            {
                sb.AppendLine($"    {pattern}");
            }

            sb.AppendLine("}");
            if (Else == null || Else.Count == 0)
            {
                return sb.ToString();
            }

            sb.AppendLine("else");
            sb.AppendLine("{");
            foreach (var pattern in Else)
            {
                sb.AppendLine($"    {pattern}");
            }

            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}