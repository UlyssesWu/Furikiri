using System.Collections.Generic;
using System.IO;
using Furikiri.AST.Statements;
using Furikiri.Echo.Language;
using Furikiri.Echo.Pass;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    /// <summary>
    /// TJS decompiler
    /// </summary>
    public class Decompiler
    {
        public Module Script { get; set; }

        internal Decompiler()
        {
        }

        public Decompiler(string path)
        {
            Script = new Module(path);
        }


        public string Decompile()
        {
            if (Script == null)
            {
                return "";
            }

            Script.Resolve();

            Dictionary<Method, BlockStatement> methods = new Dictionary<Method, BlockStatement>();

            methods.Add(Script.Methods[Script.TopLevel], DecompileObject(Script.TopLevel));
            foreach (var method in Script.Methods)
            {
                if (method.Key == Script.TopLevel)
                {
                    continue;
                }

                switch (method.Key.ContextType)
                {
                    case TjsContextType.PropertyGetter:
                    case TjsContextType.PropertySetter:
                        break;
                    case TjsContextType.Function:
                    case TjsContextType.ExprFunction:
                    case TjsContextType.TopLevel:
                        var block = DecompileObject(method.Key);
                        methods.Add(method.Value, block);
                        break;
                }
            }

            var writer = new StringWriter();
            var tjs = new TjsWriter(writer);
            tjs.WriteLicense();

            foreach (var m in methods)
            {
                tjs.WriteLine();
                tjs.WriteFunction(m.Key, m.Value);
                tjs.WriteLine();
            }

            writer.Flush();
            var result = writer.ToString();
            return result;
        }

        private BlockStatement DecompileObject(CodeObject obj)
        {
            var context = new DecompileContext(obj);
            var m = Script.Methods[obj];
            m.Compact();
            context.BuildCFG(m.Instructions);
            
            var pass1 = new RegMemberPass();
            var entry = pass1.Process(context, new BlockStatement());

            var pass2 = new ExpressionPass();
            entry = pass2.Process(context, entry);

            var pass3 = new ControlFlowPass();
            entry = pass3.Process(context, entry);

            var pass4 = new StatementCollectPass();
            entry = pass4.Process(context, entry);

            return entry;
        }
    }
}