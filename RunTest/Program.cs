using Furikiri.Echo;

namespace RunTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var defaultPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Furikiri.Tests", "Res", "unittest.tjs.comp"));
            var testPath = args.Length > 0 ? Path.GetFullPath(args[0]) : defaultPath;

            TestDecompile(testPath);
        }

        static void TestDecompile(string path, string func = "")
        {
            try
            {
                var decompiler = new Decompiler(path);
                var result = !string.IsNullOrEmpty(func) ? decompiler.Decompile(func) : decompiler.Decompile();

                Console.WriteLine(result);

                var outputPath = "decompiled.tjs";
                File.WriteAllText(outputPath, result);
                Console.WriteLine($"output path: {Path.GetFullPath(outputPath)}");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

}
