using System.Text;

namespace Furikiri.Emit
{
    public class Assembler
    {
        public const string CodeSectionBegin = "//CODE BEGIN";
        public const string CodeSectionEnd = "//CODE END";
        public const string ConstSectionBegin = "//CONST BEGIN";
        public const string ConstSectionEnd = "//CONST END";

        public bool AssembleMode { get; set; } = false;

        public string Disassemble(string path)
        {
            Module m = new Module(path);

            return Disassemble(m);
        }

        public string Disassemble(Module m)
        {
            StringBuilder sb = new StringBuilder();
            if (m.TopLevel != null)
            {
                sb.AppendLine(Disassemble(m.TopLevel));
            }

            foreach (var codeObject in m.Objects)
            {
                if (codeObject == m.TopLevel)
                {
                    continue;
                }

                sb.AppendLine(Disassemble(codeObject));
            }

            return sb.ToString();
        }

        private string Disassemble(CodeObject codeObject)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(codeObject.GetDisassembleSignatureString(AssembleMode));
            var method = codeObject.ResolveMethod();
            if (AssembleMode)
            {
                sb.AppendLine(ConstSectionBegin);
                sb.Append(method.ConstsToAssemblyDescription());
                sb.AppendLine(ConstSectionEnd);
                sb.AppendLine();
                sb.AppendLine(CodeSectionBegin);
                sb.Append(method.ToAssemblyCode(true, AssembleMode));
                sb.AppendLine(CodeSectionEnd);
            }
            else
            {
                sb.AppendLine(method.ToAssemblyCode());
            }


            return sb.ToString();
        }
    }
}