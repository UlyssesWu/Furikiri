using System;
using System.IO;
using System.Linq;
using System.Text;
using Furikiri.Emit;
using Tjs2;
using Tjs2.Engine;
using Tjs2.Sharper;

namespace Furikiri.Echo
{
    public class EchoDecompiler
    {
        private Tjs _engine;
        private Dispatch2 _dispatcher;
        private TjsByteCodeLoader _codeLoader;
        public string DisassembleWithTjsEngine(string path)
        {
            Tjs.mStorage = null;
            Tjs.Initialize();
            _engine = new Tjs();
            //Tjs.SetConsoleOutput(ConsoleOutput);

            _dispatcher = _engine.GetGlobal();
            _codeLoader = new TjsByteCodeLoader();

            var result = Disassemble(_codeLoader, _engine, path);

            _engine.Shutdown();
            Tjs.FinalizeApplication();

            return result;
        }

        public string Disassemble(string path)
        {
            Module m = new Module();
            m.LoadFromFile(path);

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

        string Disassemble(TjsByteCodeLoader loader, Tjs engine, string path)
        {
            using (var fs = new FileStream(path, FileMode.Open))
            {
                TjsBinaryStream stream = new TjsBinaryStream(fs);
                try
                {
                    var scriptBlock = loader.ReadByteCode(engine, Path.GetFileNameWithoutExtension(path), stream);

                    return DisassembleObject(scriptBlock.Objects[0].Get());

                    foreach (var scriptBlockObject in scriptBlock.Objects)
                    {
                        DisassembleObject(scriptBlockObject.Get());
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Loading {path} failed.");
                }
            }

            return null;
        }

        private string DisassembleObject(InterCodeObject obj)
        {
            var name = obj.mName;
            var t = obj.mContextType;
            var codes = obj.mCode;

            Method method = new Method(codes);
            return method.ToAssemblyCode();
        }
    }
}
