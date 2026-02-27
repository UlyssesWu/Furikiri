using System.Text;
using System.Linq;

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
            m.Resolve();
            if (m.TopLevel != null)
            {
                sb.AppendLine(Disassemble(m, m.TopLevel));
            }

            foreach (var codeObject in m.Objects)
            {
                if (codeObject == m.TopLevel)
                {
                    continue;
                }

                sb.AppendLine(Disassemble(m, codeObject));
            }

            return sb.ToString();
        }

        private string Disassemble(Module module, CodeObject codeObject)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(codeObject.GetDisassembleSignatureString(AssembleMode));
            AppendPropertyAssociation(module, codeObject, sb);
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
                var asm = method.ToAssemblyCode();
                if (!string.IsNullOrEmpty(asm))
                {
                    sb.Append(asm);
                }
            }


            return sb.ToString();
        }

        private static void AppendPropertyAssociation(Module module, CodeObject codeObject, StringBuilder sb)
        {
            if (codeObject.ContextType == TjsContextType.Property)
            {
                if (module.Properties.TryGetValue(codeObject.Name, out var property))
                {
                    var getter = property.Getter?.Object?.GetDisassembleSignatureString() ?? "<none>";
                    var setter = property.Setter?.Object?.GetDisassembleSignatureString() ?? "<none>";
                    sb.AppendLine($"// related getter: {getter}");
                    sb.AppendLine($"// related setter: {setter}");
                }

                return;
            }

            if (codeObject.ContextType is TjsContextType.PropertyGetter or TjsContextType.PropertySetter)
            {
                var ownerProperty = module.Properties.Values.FirstOrDefault(p =>
                    p.Getter?.Object == codeObject || p.Setter?.Object == codeObject);
                if (ownerProperty != null)
                {
                    sb.AppendLine($"// owner property: {ownerProperty.Name} 0x{ownerProperty.Object.GetHashCode():X8}");
                    sb.AppendLine();
                }
            }
        }
    }
}