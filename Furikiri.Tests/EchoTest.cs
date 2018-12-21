using System;
using System.IO;
using Furikiri.Echo;
using Furikiri.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Furikiri.Tests
{
    [TestClass]
    public class EchoTest
    {
        [TestMethod]
        public void TestDecompile()
        {
            EchoDecompiler decompiler = new EchoDecompiler();
            var code = decompiler.Disassemble("..\\..\\Res\\startup.tjs");
            File.WriteAllText("out.tjsasm", code);
        }

        [TestMethod]
        public void TestLoadTjs()
        {
            var path = "..\\..\\Res\\startup.tjs";
            Module m = new Module();
            m.LoadFromFile(path);
        }
    }
}
