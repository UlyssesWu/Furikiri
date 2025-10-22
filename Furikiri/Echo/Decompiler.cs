using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        internal string Decompile(string objName)
        {
            if (Script == null)
            {
                return "";
            }

            Script.Resolve();

            Dictionary<Method, BlockStatement> methods = new Dictionary<Method, BlockStatement>();

            //methods.Add(Script.Methods[Script.TopLevel], DecompileObject(Script.TopLevel));

            var method = Script.Methods.FirstOrDefault(m => m.Key.Name == objName);
            var block = DecompileObject(method.Key);
            methods.Add(method.Value, block);
            
            var writer = new StringWriter();
            var tjs = new TjsWriter(writer) {MethodRefs = methods};
            tjs.WriteLicense();

            foreach (var m in methods)
            {
                if (m.Key.IsLambda)
                {
                    continue;
                }

                tjs.WriteLine();
                tjs.WriteFunction(m.Key, m.Value);
                tjs.WriteLine();
            }

            writer.Flush();
            var result = writer.ToString();
            return result;
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
            var tjs = new TjsWriter(writer){MethodRefs = methods};
            tjs.WriteLicense();

            foreach (var m in methods)
            {
                if (m.Key.IsLambda)
                {
                    continue;
                }

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
            
            // Pass 1: Register members
            var pass1 = new RegMemberPass();
            var entry = pass1.Process(context, new BlockStatement());

            // Pass 2: Build expressions (generates Phi nodes)
            var pass2 = new ExpressionPass();
            entry = pass2.Process(context, entry);

            // Pass 3: Expression propagation and Phi elimination
            var pass3 = new ExpressionPropagationPass();
            entry = pass3.Process(context, entry);

            // Pass 4: Control flow analysis
            var pass4 = new ControlFlowPass();
            entry = pass4.Process(context, entry);

            // Pass 5: Collect statements
            var pass5 = new StatementCollectPass();
            entry = pass5.Process(context, entry);

            m.Vars = context.Vars;
            
            return entry;
        }
    }
}