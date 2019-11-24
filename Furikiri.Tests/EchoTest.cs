using System.IO;
using System.Linq;
using Furikiri.AST.Statements;
using Furikiri.Echo;
using Furikiri.Echo.Language;
using Furikiri.Echo.Pass;
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
            //var result = decompiler.Decompile();
            //var result = decompiler.Decompile("global");
            //var result = decompiler.Decompile("Test"); //there is a bug at [var b3 = b2 || b;] to be solved only by data flow analysis
            // B1 -> B2 -> B3, B1 -> B3, B3.From = B1 & B2, B3.Input = flag, B1.Output & B1.Def = flag, B2.Output & B2.Def = flag => flag = φ
            var result = decompiler.Decompile("TestLoop"); //bug: the generated expression is wrong at [v4 ++ += 2]
            //TODO: v4++ shouldn't be kept in registers, just pend to expList and leave v4 in register
            //Maybe block hiding is a bad idea
            //For Condition: if first Block can be merged (Propagation) into 1 statement, then it can be the condition, otherwise no condition.
            //Should be able to determine whether a slot is not used anymore using data flow (Dead) so can perform propagation
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