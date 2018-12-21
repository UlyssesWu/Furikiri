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
        public void TestDisassemble()
        {
            Assembler assembler = new Assembler();
            var code = assembler.Disassemble("..\\..\\Res\\startup.tjs");
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
