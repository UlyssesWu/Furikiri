using System;
using System.IO;
using System.Linq;
using Furikiri.Emit;

namespace Furikiri.Girigiri
{
    class Program
    {
        private static Assembler _asm = new Assembler();

        static void Main(string[] args)
        {
            Console.WriteLine("Furikiri TJS2 Disassembler");
            Console.WriteLine("by Ulysses, wdwxy12345@gmail.com");
            Console.WriteLine();

            if (args.Length <= 0)
            {
                PrintHelp();
                return;
            }

            foreach (string s in args)
            {
                if (Directory.Exists(s)) //disasm dir
                {
                    var list = Directory.EnumerateFiles(s, "*.tjs").Where(ss => ss.ToLowerInvariant().EndsWith(".tjs"))
                        .ToList();
                    foreach (var p in list)
                    {
                        Disassemble(p);
                    }
                }
                else if (File.Exists(s))
                {
                    Disassemble(s);
                }
            }

            Console.WriteLine("All done!");
        }

        private static void Disassemble(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            Console.Write($"Process {path} ...");
            try
            {
                var result = _asm.Disassemble(new Module(path));
                File.WriteAllText(Path.ChangeExtension(path, ".tjsasm"), result);
                Console.WriteLine("Done.");
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed!");
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"Usage: .exe <TJS2 dir or file>
The decompile feature is in dev.");
        }
    }
}