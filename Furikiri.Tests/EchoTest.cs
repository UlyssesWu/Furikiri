using System;
using Furikiri.Echo;
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
            decompiler.Decompile("..\\..\\Res\\startup.tjs");
        }
    }
}
