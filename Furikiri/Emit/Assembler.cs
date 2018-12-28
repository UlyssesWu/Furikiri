using System.Text;

namespace Furikiri.Emit
{
    public class Assembler
    {
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
            sb.AppendLine(codeObject.GetDisassembleSignatureString());
            var method = codeObject.ResolveMethod();
            sb.AppendLine(method.ToAssemblyCode());
            return sb.ToString();
        }
    }
}
