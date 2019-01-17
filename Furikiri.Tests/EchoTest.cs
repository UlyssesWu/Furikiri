using System.Collections.Generic;
using System.IO;
using Furikiri.Echo;
using Furikiri.Echo.Patterns;
using Furikiri.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Tjs2;
using Tjs2.Engine;
using Tjs2.Sharper;

namespace Furikiri.Tests
{
    [TestClass]
    public class EchoTest
    {
        [TestMethod]
        public void TestDisassemble()
        {
            var path = "..\\..\\Res\\Initialize.tjs.comp";
            Assembler assembler = new Assembler();
            var code = assembler.Disassemble(path);
            File.WriteAllText("out.tjsasm", code);
        }

        [TestMethod]
        public void TestLoadTjs()
        {
            var path = "..\\..\\Res\\startup.tjs";
            Module m = new Module(path);

            var method = m.TopLevel.ResolveMethod();
            var offset = 0;
            foreach (var ins in method.Instructions)
            {
                Assert.AreEqual(ins.Offset, offset);
                offset += ins.Size;
            }
        }

        [TestMethod]
        public void TestLoadTjs2()
        {
            var path = "..\\..\\Res\\Initialize.tjs.comp";
            Module m = new Module(path);
            var method = m.TopLevel.ResolveMethod();
            var offset = 0;
            foreach (var ins in method.Instructions)
            {
                Assert.AreEqual(ins.Offset, offset);
                offset += ins.Size;
            }
        }

        [TestMethod]
        public void TestDecompileTjs()
        {
            var path = "..\\..\\Res\\Initialize.tjs.comp";
            Decompiler decompiler = new Decompiler(path);
            decompiler.Decompile();
            return;
            var KAGLoadScript = decompiler.Script.Objects.Find(c => c.Name == "KAGLoadScript");
            var argC = KAGLoadScript.FuncDeclArgCount;
            var argD = KAGLoadScript.FuncDeclCollapseBase;
            var argU = KAGLoadScript.FuncDeclUnnamedArgArrayBase;
            var vR = KAGLoadScript.VariableReserveCount;
            var vM = KAGLoadScript.MaxVariableCount;
            foreach (var tjsVariant in KAGLoadScript.Variants)
            {
                var v = tjsVariant;
            }

            var s = KAGLoadScript.SourcePosArray;
        }

        [TestMethod]
        public void TestDecompileBlock()
        {
            var path = "..\\..\\Res\\Initialize.tjs.comp";
            Module md = new Module(path);
            var mt = md.TopLevel.ResolveMethod();
            mt.Compact();

            DecompileContext context = new DecompileContext(md.TopLevel);
            context.ScanBlocks(mt.Instructions);
            context.ComputeDominators();
            context.ComputeNaturalLoops();

            context.Detectors = new List<DetectHandler>()
            {
                RegMemberPattern.Match,
                UnaryOpPattern.Match,
                BinaryOpPattern.Match,
                CallPattern.Match,
                DeletePattern.Match,
                GotoPattern.Match
            };

            context.FillInBlocks(mt.Instructions);

            context.IntervalAnalysisDoWhilePass();

            context.LifetimeAnalysis();
        }

        //DO NOT WORK
        [TestMethod]
        public void TestCompileTjs()
        {
            var path = "..\\..\\Res\\Initialize.tjs";
            Tjs.mStorage = null;
            Tjs.Initialize();
            Tjs scriptEngine = new Tjs();
            Compiler c = new Compiler(scriptEngine);
            using (var fs = File.Create("out.tjsbin"))
            {
                BinaryStream bs = new TjsBinaryStream(fs);
                c.Compile(File.ReadAllText(path), false, false, bs);
            }
        }
    }
}