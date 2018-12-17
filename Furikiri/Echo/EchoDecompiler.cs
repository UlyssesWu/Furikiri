using System;
using System.IO;
using System.Linq;
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
        public void Decompile(string path)
        {
            Tjs.mStorage = null;
            Tjs.Initialize();
            _engine = new Tjs();
            //Tjs.SetConsoleOutput(ConsoleOutput);

            _dispatcher = _engine.GetGlobal();
            _codeLoader = new TjsByteCodeLoader();

            Decompile(_codeLoader, _engine, path);
            
            _engine.Shutdown();
            Tjs.FinalizeApplication();
        }

        void Decompile(TjsByteCodeLoader loader, Tjs engine, string path)
        {
            using (var fs = new FileStream(path, FileMode.Open))
            {
                TjsBinaryStream stream = new TjsBinaryStream(fs);
                try
                {
                    var scriptBlock = loader.ReadByteCode(engine, Path.GetFileNameWithoutExtension(path), stream);
                    foreach (var scriptBlockObject in scriptBlock.Objects)
                    {
                        DecompileObject(scriptBlockObject.Get());
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Loading {path} failed.");
                }
            }
        }

        private void DecompileObject(InterCodeObject obj)
        {
            var name = obj.mName;
            var t = obj.mContextType;
            var codes = obj.mCode;
        }
    }
}
