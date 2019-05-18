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

        public Dictionary<CodeObject, Method> Methods { get; set; } = new Dictionary<CodeObject, Method>();
        
        public Decompiler()
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

            Methods[Script.TopLevel] = Script.TopLevel.ResolveMethod();

            foreach (var obj in Script.Objects)
            {
                if (obj == Script.TopLevel)
                {
                    continue;
                }

                Methods[obj] = obj.ResolveMethod();
            }

            var statement = DecompileObject(Script.TopLevel);
            
            var writer = new StringWriter();
            var tjs = new TjsWriter(writer);
            tjs.WriteLicense();
            tjs.WriteLine();
            tjs.WriteBlock(statement);
            writer.Flush();
            var result = writer.ToString();
            return result;
        }

        private BlockStatement DecompileObject(CodeObject obj)
        {
            var context = new DecompileContext(obj);
            var m = Methods[obj];
            m.Compact();
            context.ScanBlocks(m.Instructions);
            context.ComputeDominators();
            context.ComputeNaturalLoops();

            context.FillInBlocks(m.Instructions);

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