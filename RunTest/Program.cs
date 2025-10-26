using Furikiri.Echo;

namespace RunTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var testPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Furikiri.Tests", "Res", "unittest.tjs.comp"));

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
