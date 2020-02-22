using System;
using System.IO;
using System.Linq;
using System.Text;
using Furikiri.Echo;
using Furikiri.Emit;
using McMaster.Extensions.CommandLineUtils;

namespace Furikiri.CLI
{
    class Program
    {
        private static Assembler _asm = new Assembler();

        static void Main(string[] args)
        {
            Console.WriteLine("Furikiri TJS2 Disassembler/Decompiler");
            Console.WriteLine("by Ulysses, wdwxy12345@gmail.com");
            Console.WriteLine();

            var app = new CommandLineApplication();
            app.OptionsComparison = StringComparison.OrdinalIgnoreCase;

            //help
            app.HelpOption();
            app.ExtendedHelpText = PrintHelp();

            //options
            var optDis = app.Option("-da|--disassemble", "Disassemble byte code", CommandOptionType.NoValue);
            var optDec = app.Option("-d|--dec", "Decompile byte code", CommandOptionType.NoValue);
            var optPrint = app.Option("-p|--print", "Print result", CommandOptionType.NoValue);

            //args
            var argPath =
                app.Argument("Files", "File paths", multipleValues: true);

            app.OnExecute(() =>
            {
                var print = optPrint.HasValue();
                foreach (string s in argPath.Values)
                {
                    if (Directory.Exists(s)) //disasm dir
                    {
                        var list = Directory.EnumerateFiles(s, "*.tjs")
                            .Where(ss => ss.ToLowerInvariant().EndsWith(".tjs"))
                            .ToList();
                        foreach (var p in list)
                        {
                            if (optDec.HasValue())
                            {
                                Decompile(p, print);
                            }
                            else
                            {
                                Disassemble(p, print);
                            }
                        }
                    }
                    else if (File.Exists(s))
                    {
                        if (optDec.HasValue())
                        {
                            Decompile(s, print);
                        }
                        else
                        {
                            Disassemble(s, print);
                        }
                    }
                }
            });

            app.Execute(args);
            Console.WriteLine("All done!");
#if DEBUG
            Console.ReadLine();
#endif
        }

        private static void Decompile(string path, bool print = false)
        {
            Decompiler decompiler = new Decompiler(path);
            var result = decompiler.Decompile();
            if (print)
            {
                Console.WriteLine("// File: " + path);
                Console.WriteLine(result);
                Console.WriteLine();
            }
            else
            {
                path = Path.ChangeExtension(path, Path.GetExtension(path) == ".tjs" ? ".dec.tjs" : ".tjs");
                File.WriteAllText(path, result);
            }
        }

        private static void Disassemble(string path, bool print = false)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Console.WriteLine("// File: " + path);
            try
            {
                var result = _asm.Disassemble(new Module(path));
                if (print)
                {
                    Console.WriteLine(result);
                }
                else
                {
                    File.WriteAllText(Path.ChangeExtension(path, ".tjsasm"), result);
                }

                Console.WriteLine("Done.");
            }
            catch (Exception e)
            {
                Console.WriteLine("Failed!");
            }
        }

        private static string PrintHelp()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(@"Examples: 
  Girigiri init.tjs");
            return sb.ToString();
        }
    }
}