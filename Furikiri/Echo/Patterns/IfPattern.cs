using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// If
    /// </summary>
    class IfPattern : IBranch
    {
        public bool Terminal { get; set; }
        public int Length { get; }
        public IExpression Condition { get; set; }
        public BranchType BranchType { get; } = BranchType.If;
        public List<Block> Content { get; } = new List<Block>();
        public List<Block> Else { get; } = new List<Block>();

        public Block To { get; set; }


        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"if ({Condition})");
            sb.AppendLine("{");
            sb.AppendLine("}");
            //if (InsElse == null || InsElse.Count == 0)
            //{
            //    return sb.ToString();
            //}

            sb.AppendLine("else");
            sb.AppendLine("{");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}