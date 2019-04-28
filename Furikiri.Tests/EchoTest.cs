using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Furikiri.AST.Statements;
using Furikiri.Echo;
using Furikiri.Echo.Language;
using Furikiri.Echo.Pass;
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

            //context.IntervalAnalysisDoWhilePass();

            //context.LifetimeAnalysis();

            var b = context.Blocks;
        }

        [TestMethod]
        public void TestDecompileBlock2()
        {
            var path = "..\\..\\Res\\Initialize.tjs.comp";
            Module md = new Module(path);
            var mt = md.TopLevel.ResolveMethod();
            mt.Compact();

            DecompileContext context = new DecompileContext(md.TopLevel);
            context.ScanBlocks(mt.Instructions);
            context.ComputeDominators();
            context.ComputeNaturalLoops();

            context.FillInBlocks(mt.Instructions);

            var pass1 = new RegMemberPass();
            var entry = pass1.Process(context, new BlockStatement());
            var pass2 = new ExpressionPass();
            entry = pass2.Process(context, entry);

            var b = context.Blocks[1];
            var s1 = b.Statements.FirstOrDefault();

            var pass3 = new ControlFlowPass();
            entry = pass3.Process(context, entry);
            var c = entry.Statements.Count;
            foreach (var st in entry.Statements)
            {
                var s = st;
            }

            var pass4 = new StatementCollectPass();
            entry = pass4.Process(context, entry);

            foreach (var statement in entry.Statements)
            {
                var s = statement;
            }

            var sWriter = new StringWriter();
            TjsWriter writer = new TjsWriter(sWriter);
            writer.WriteBlock(entry);
            sWriter.Flush();
            var result = sWriter.ToString();
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