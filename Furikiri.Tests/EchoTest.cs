using System;
using System.IO;
using System.Linq;
using Furikiri.AST.Statements;
using Furikiri.Compile;
using Furikiri.Echo;
using Furikiri.Echo.Language;
using Furikiri.Echo.Pass;
using Furikiri.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;
//using Tjs2;
//using Tjs2.Engine;
//using Tjs2.Sharper;

namespace Furikiri.Tests
{
    [TestClass]
    public class EchoTest
    {
        [TestInitialize]
        public void UseLegacyNamesForExistingSemanticAssertions()
        {
            // 既有语义断言使用旧快照；专门的命名测试同时覆盖新默认和兼容模式。
            Config.UseLegacyRegisterVariableNames = true;
        }

        [TestMethod]
        public void TestDisassemble()
        {
            var path = "..\\..\\..\\Res\\Initialize.tjs.comp";
            Assembler assembler = new Assembler(){AssembleMode = true};
            var code = assembler.Disassemble(path);
            //File.WriteAllText("out.tjsasm", code);
            File.WriteAllText("out-asm.tjsasm", code);
            //TODO: detect this when Data is self e.g. const %1, *5 // *5 = (object) this
        }

        [TestMethod]
        public void TestParseAsm()
        {
            var text = File.ReadAllText("out-asm.tjsasm");
            var tokens = TjsAsmTokenizer.Instance.Tokenize(text);
            
            foreach (var token in tokens.Skip(1000).Take(100))
            {
                var t = token;
            }
        }

        [TestMethod]
        public void TestLoadTjs()
        {
            var path = "..\\..\\..\\Res\\Initialize.tjs.comp";
            //var path = "..\\..\\Res\\startup.tjsbc";
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
            var path = "..\\..\\..\\Res\\Initialize.tjs.comp";
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
        public void TestDecompileUnitTest()
        {
            var path = "..\\..\\..\\Res\\unittest.tjs.comp";
            Decompiler decompiler = new Decompiler(path);
            var result = decompiler.Decompile("TestTry");
            Console.WriteLine(result);
        }

        [TestMethod]
        public void TestSemanticControlFlowRecovery()
        {
            var path = "..\\..\\..\\Res\\SemanticControlFlow.tjs.comp";
            var result = new Decompiler(path).Decompile();

            StringAssert.Contains(result, "var owner;");
            StringAssert.Contains(result, "var number;");
            StringAssert.Contains(result,
                "if (p4 === void || typeof p3.isBitmap == \"Object\" && !p3.isBitmap())");
            StringAssert.Contains(result, "if (p3 !== void && p3.pos !== void)");
            StringAssert.Contains(result,
                "if (p3.mode == \"\" || p3.mode == \"pile\" || p3.mode == \"alpha\")");
            StringAssert.Contains(result, "else if (p3.mode == \"addalpha\")");
            StringAssert.Contains(result, "face = dfAddAlpha;");
            StringAssert.Contains(result, "face = dfOpaque;");
            StringAssert.Contains(result, "done = 1;");
            StringAssert.Contains(result, "p4 !== void && isvalid p3");
            StringAssert.Contains(result, "p3.removeHook(p4);");
            StringAssert.Contains(result, "term();");
            StringAssert.Contains(result, "finish();");
            StringAssert.Contains(result,
                "p3 !== void && p3.charAt(0) == \";\" && p3.charAt(1) == \"!\"");
            StringAssert.Contains(result, "for (var v5 = 0; v5 < p4.count; v5++)");
            StringAssert.Contains(result,
                "p4[v5] = \".\" + p4[v5] + ((p4[v5].indexOf(\"=\") < 0) ? \"=true\" : \"\");");
            var optionsBody = SliceBetween(result, "function options", "function position");
            Assert.IsFalse(optionsBody.Contains("continue;", StringComparison.Ordinal),
                "三元表达式的取值分支不应被误恢复为 continue");
            StringAssert.Contains(result, "if (p3 !== void && +p3)");
            StringAssert.Contains(result, "act();");
            StringAssert.Contains(result, "return -4;");
            StringAssert.Contains(result,
                "angle = ((p4 === void) ? (p3 ? 2700 : 0) : +p4);");
            StringAssert.Contains(result,
                "selected = (p3 ? p4 : ((p5 > p6) ? p6 : p5));");
            StringAssert.Contains(result, "return (p3 || (p4 && p5));");
            StringAssert.Contains(result, "if (p3 == null)");
            Assert.IsFalse(result.Contains("Furikiri.Emit.TjsObject", StringComparison.Ordinal),
                "TJS2 的 null 对象常量不应泄漏为 CLR 类型名");
            StringAssert.Contains(result, "p4 == KEY_LEFT || p4 == KEY_PRIOR");
            StringAssert.Contains(result, "p4 == KEY_RIGHT || p4 == KEY_NEXT");
            var nestedEqualityBody = SliceBetween(
                result, "function nestedEqualityDispatch", "function nestedReverseLoops");
            StringAssert.Contains(nestedEqualityBody, "if (p3 == \"click\")");
            StringAssert.Contains(nestedEqualityBody, "if (p4 == TARGET_YES)");
            StringAssert.Contains(nestedEqualityBody, "else if (p4 == TARGET_NO)");
            Assert.AreEqual(1, CountOccurrences(nestedEqualityBody, "first();"),
                "嵌套相等比较的首个分支副作用不能被提升或重复输出");
            StringAssert.Contains(result, "if (p3 == 1 || p3 == 2)");
            StringAssert.Contains(result, "second();");
            StringAssert.Contains(result, "third();");
            StringAssert.Contains(result, "afterSwitch();");
            StringAssert.Contains(result, "if (v9)");
            StringAssert.Contains(result, "v7 += p6[v9 >> 2];");
            StringAssert.Contains(result,
                "if (v12 == 1 && (v11 != 3 || v9 <= 4))");
            StringAssert.Contains(result, "v7 += p4[v12] + p5[v11];");
            StringAssert.Contains(result, "v7 += p5[v11];");
            var switchLoopBody = result[result.IndexOf("function switchLoop", StringComparison.Ordinal)..];
            Assert.AreEqual(1, CountOccurrences(switchLoopBody, "var v8 = 0;"),
                "循环内重置已有标志变量时不应重复输出 var 声明");
            StringAssert.Contains(result, "if (v9 >= \"0\" && v9 <= \"9\")");
            StringAssert.Contains(result, "v7 += p4[+v9];");
            StringAssert.Contains(result, "v7 += v9;");
            var delayedForBody = SliceBetween(result, "function delayedForInitializer", "function rangeBreak");
            StringAssert.Contains(delayedForBody, "for (var v5 = 0; v5 < count_; v5++)");
            StringAssert.Contains(delayedForBody, "continue;");
            Assert.IsFalse(delayedForBody.Contains("if (p3[v5] == \"\")\r\n        {\r\n        }", StringComparison.Ordinal),
                "循环前还有缓存赋值时，也应按步进变量找到初始化并恢复 continue");
            var assignmentLoopBody = SliceBetween(result, "function assignmentConditionLoop", "function callOnce");
            StringAssert.Contains(assignmentLoopBody, "while ((v5 = p3[v4++]) !== void)");
            StringAssert.Contains(assignmentLoopBody, "touch();");
            StringAssert.Contains(assignmentLoopBody, "break;");
            StringAssert.Contains(assignmentLoopBody, "continue;");
            var assignedGuardBody = SliceBetween(
                result, "function assignmentShortCircuitGuard", "function bitwiseMask");
            var combinedAssignedGuard = assignedGuardBody.Contains(
                "if (v6 && (name_ = v6.name) != \"\")", StringComparison.Ordinal);
            var nestedAssignedGuard = assignedGuardBody.Contains("if (v6)", StringComparison.Ordinal) &&
                                      assignedGuardBody.Contains("name_ = v6.name;", StringComparison.Ordinal) &&
                                      assignedGuardBody.Contains("if (name_ != \"\")", StringComparison.Ordinal);
            Assert.IsTrue(combinedAssignedGuard || nestedAssignedGuard,
                "带赋值的短路条件应恢复为组合式或等价的嵌套式");
            Assert.IsTrue(assignedGuardBody.IndexOf("name_ = v6.name", StringComparison.Ordinal) <
                          assignedGuardBody.IndexOf("consume(v6);", StringComparison.Ordinal),
                "带赋值的短路条件必须继续保护后续副作用");
            StringAssert.Contains(result,
                "if (p3 == \"\" || p4[p3] === void || (states_ = p4[p3].states) === void)");
            StringAssert.Contains(result, "consume(states_);");
            var conditionalLatchBody = SliceBetween(
                result, "function conditionalLatchLoop", "function delayedForInitializer");
            StringAssert.Contains(conditionalLatchBody, "while (v4 < count_)");
            StringAssert.Contains(conditionalLatchBody, "count_--;");
            StringAssert.Contains(conditionalLatchBody, "else\r\n            {");
            StringAssert.Contains(conditionalLatchBody, "v4++;");
            Assert.IsFalse(conditionalLatchBody.Contains("for (", StringComparison.Ordinal),
                "多个条件化回边不能被提升成带无条件步进的 for");
            var compoundDoWhileBody = SliceBetween(
                result, "function compoundDoWhile", "function conditionalLatchLoop");
            StringAssert.Contains(compoundDoWhileBody,
                "while (v5 < p4 && p3[v5] == \"\")");
            Assert.IsFalse(compoundDoWhileBody.Contains("v5 < p4;", StringComparison.Ordinal),
                "do-while 的短路前缀不能作为循环体内的裸比较输出");
            var emptySwitchBody = SliceBetween(
                result, "function emptySwitchCase", "function equalityDefault");
            StringAssert.Contains(emptySwitchBody, "if (v5 == \"\\\\\")");
            StringAssert.Contains(emptySwitchBody, "else if (v5 == \"k\")");
            Assert.IsFalse(emptySwitchBody.Contains("v5 == \"\\\\\";", StringComparison.Ordinal),
                "空 case 的比较不能脱离 switch 成为裸表达式");
            Assert.IsFalse(emptySwitchBody.Contains("v5 == \"\\\\\" || v5 == \"k\"", StringComparison.Ordinal),
                "跳出 switch 的空 case 不能与后续有副作用的 case 合并");
            var guardedDeleteBody = SliceBetween(
                result, "function guardedDelete", "function initializedSideEffectGuard");
            StringAssert.Contains(guardedDeleteBody, "if (p3[p4] !== void)");
            StringAssert.Contains(guardedDeleteBody, "delete p3[p4];");
            var shortCircuitLoopBody = SliceBetween(
                result, "function loopShortCircuitBody", "function mode");
            StringAssert.Contains(shortCircuitLoopBody, "if (p5 || p4 === void");
            StringAssert.Contains(shortCircuitLoopBody, "v7.run === void");
            StringAssert.Contains(shortCircuitLoopBody, "p4 === void && !v7.done");
            StringAssert.Contains(shortCircuitLoopBody, "mark(v7);");
            StringAssert.Contains(shortCircuitLoopBody, "if (!interrupted)");
            StringAssert.Contains(shortCircuitLoopBody, "if (timerEnabled)");
            StringAssert.Contains(shortCircuitLoopBody, "queued(v7);");
            StringAssert.Contains(shortCircuitLoopBody, "direct(v7);");
            var initializedSideEffectBody = SliceBetween(
                result, "function initializedSideEffectGuard", "function loopShortCircuitBody");
            StringAssert.Contains(initializedSideEffectBody,
                "if (off_ && setVisible(0) || on_ && setVisible(1))");
            StringAssert.Contains(initializedSideEffectBody, "return -3;");
            StringAssert.Contains(initializedSideEffectBody, "afterVisibility();");
            var sideEffectGuardBody = SliceBetween(
                result, "function sideEffectGuard", "function sideEffectWithGuard");
            StringAssert.Contains(sideEffectGuardBody,
                "if (p3 && setVisible(0) || p4 && setVisible(1))");
            Assert.IsTrue(sideEffectGuardBody.IndexOf("setVisible(0)", StringComparison.Ordinal) <
                          sideEffectGuardBody.IndexOf("setVisible(1)", StringComparison.Ordinal),
                "短路链中的调用顺序必须与 CFG 一致");
            StringAssert.Contains(sideEffectGuardBody, "afterVisibility();");
            var sideEffectWithGuardBody = SliceBetween(
                result, "function sideEffectWithGuard", "function switchLoop");
            StringAssert.Contains(sideEffectWithGuardBody,
                "if (p4 && setVisible(0) || p5 && setVisible(1))");
            StringAssert.Contains(sideEffectWithGuardBody, "afterVisibility();");
            var gatedMultiWayBody = SliceBetween(
                result, "function gatedMultiWay", "function groupedSwitch");
            StringAssert.Contains(gatedMultiWayBody, "if (p3)");
            StringAssert.Contains(gatedMultiWayBody, "if (p4 == 1)");
            StringAssert.Contains(gatedMultiWayBody, "else if (p4 == 2)");
            Assert.IsTrue(gatedMultiWayBody.LastIndexOf("afterChoice();", StringComparison.Ordinal) >
                          gatedMultiWayBody.LastIndexOf("else if (p4 == 2)", StringComparison.Ordinal),
                "外围 gate 结束后才应执行公共继续语句");
            var multiWayNoDefaultBody = SliceBetween(
                result, "function multiWayNoDefault", "function nestedEqualityDispatch");
            StringAssert.Contains(multiWayNoDefaultBody, "if (p3 == 1)");
            StringAssert.Contains(multiWayNoDefaultBody, "else if (p3 == 2)");
            StringAssert.Contains(multiWayNoDefaultBody, "afterChoice();");
            var tryChoiceStart = result.IndexOf("function tryThenElseChoice", StringComparison.Ordinal);
            Assert.IsTrue(tryChoiceStart >= 0, "找不到 try/catch 分支回归函数");
            var tryChoiceBody = result[tryChoiceStart..];
            StringAssert.Contains(tryChoiceBody, "catch(v4)");
            StringAssert.Contains(tryChoiceBody, "failed(v4);");
            StringAssert.Contains(tryChoiceBody, "else if (p3 == 2)");
            StringAssert.Contains(tryChoiceBody, "afterChoice();");
            var nestedLoopsBody = SliceBetween(result, "function nestedReverseLoops", "function nullGuard");
            StringAssert.Contains(nestedLoopsBody, "for (var v5 = p3.count - 1; v5 >= 0; v5--)");
            StringAssert.Contains(nestedLoopsBody, "for (var v7 = v6.count - 1; v7 >= 0; v7--)");
            StringAssert.Contains(nestedLoopsBody, "if (v6[v7] != \"\")");
            StringAssert.Contains(nestedLoopsBody, "v4++;");
            StringAssert.Contains(result, "if (v6 == \".\" || v6 == \"e\")");
            StringAssert.Contains(result, "break;");
            StringAssert.Contains(result, "p3 = p3 & ~(p4 | p5);");
            StringAssert.Contains(result, "if (p3.top === void)");
            StringAssert.Contains(result, "if (p3.left === void)");
            StringAssert.Contains(result, "if (p3.right === void)");
            StringAssert.Contains(result,
                "handlers = %[\r\n            \"pimage\" => SemanticControlFlow.loadPartialImage,");
            StringAssert.Contains(result,
                "\r\n            \"ptext\" => SemanticControlFlow.drawReconstructibleText\r\n        ];");
            StringAssert.Contains(result,
                "if (p3 === void || p3 == \"\" || p3.substr(0, 3) == \"eye\" && +p3.substr(3) == p4 || p3.substr(0, 3) == \"lip\" && +p3.substr(3) == p5)");
            StringAssert.Contains(result, "var object_ = p3[p4].object;");
            StringAssert.Contains(result,
                "if (object_ === void || !isvalid object_ || object_.visible && object_.enabled)");
            StringAssert.Contains(result, "consume((new PSBFile(p3)).root);");
            StringAssert.Contains(result,
                "if (p3 != \"\" && p4 == p3 || p3 == \"\" && probe(p5) != \"\")");
            Assert.IsFalse(result.Contains("/*phi:", StringComparison.Ordinal),
                "可结构化的嵌套三元表达式不应退化为 Phi 占位符");
            Assert.AreEqual(1, CountOccurrences(result, "p3(p4)"),
                "有返回值的动态调用不应同时作为独立语句重复输出");
        }

        [TestMethod]
        public void TestSameLineOpeningBraceStyle()
        {
            var path = "..\\..\\..\\Res\\SemanticControlFlow.tjs.comp";
            var originalStyle = Config.OpeningBraceOnNewLine;
            try
            {
                Config.OpeningBraceOnNewLine = false;
                var result = new Decompiler(path).Decompile();

                StringAssert.Contains(result, "class SemanticControlFlow {");
                StringAssert.Contains(result, "function guard(p3, p4) {");
                StringAssert.Contains(result,
                    "if (p4 === void || typeof p3.isBitmap == \"Object\" && !p3.isBitmap()) {");
                StringAssert.Contains(result, "else if (p3.mode == \"addalpha\") {");
                StringAssert.Contains(result, "try {");
                StringAssert.Contains(result, "catch(v4) {");
            }
            finally
            {
                Config.OpeningBraceOnNewLine = originalStyle;
            }
        }

        [TestMethod]
        public void TestSequentialVariableNamingStyle()
        {
            var path = "..\\..\\..\\Res\\SemanticControlFlow.tjs.comp";
            var originalStyle = Config.UseLegacyRegisterVariableNames;
            try
            {
                Config.UseLegacyRegisterVariableNames = false;
                var sequential = new Decompiler(path).Decompile();
                StringAssert.Contains(sequential, "function guard(a0, a1)");
                StringAssert.Contains(sequential,
                    "if (a1 === void || typeof a0.isBitmap == \"Object\" && !a0.isBitmap())");
                StringAssert.Contains(sequential, "function options(a0, a1)");
                StringAssert.Contains(sequential, "for (var v0 = 0; v0 < a1.count; v0++)");
                StringAssert.Contains(sequential, "function switchLoop(a0, a1, a2, a3)");
                StringAssert.Contains(sequential, "var v0 = \"\";");
                StringAssert.Contains(sequential, "function collapseNames(a0, __params1*)");
                var collapseBody = SliceBetween(
                    sequential, "function collapseNames", "function compoundDoWhile");
                StringAssert.Contains(collapseBody, "var v0 = a0;");
                StringAssert.Contains(collapseBody, "var v1 = __params1.count;");
                Assert.IsFalse(sequential.Contains("function guard(p3, p4)", StringComparison.Ordinal));

                Config.UseLegacyRegisterVariableNames = true;
                var legacy = new Decompiler(path).Decompile();
                StringAssert.Contains(legacy, "function guard(p3, p4)");
                StringAssert.Contains(legacy, "for (var v5 = 0; v5 < p4.count; v5++)");
            }
            finally
            {
                Config.UseLegacyRegisterVariableNames = originalStyle;
            }
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var offset = 0;
            while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += value.Length;
            }

            return count;
        }

        private static string SliceBetween(string text, string startMarker, string endMarker)
        {
            var start = text.IndexOf(startMarker, StringComparison.Ordinal);
            var end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start, $"找不到函数片段：{startMarker}");
            return text[start..end];
        }

        [TestMethod]
        public void TestDecompileTjs()
        {
            var path = "..\\..\\..\\Res\\Initialize.tjs.comp";
            //var path = "..\\..\\..\\Res\\startup.tjs";
            Decompiler decompiler = new Decompiler(path);
            //var result = decompiler.Decompile();
            var result = decompiler.Decompile("global");
            //var result = decompiler.Decompile("autopath");
            //var result = decompiler.Decompile("countLayerMetrics");
            //var result = decompiler.Decompile("Test"); //there is a bug at [var b3 = b2 || b;] to be solved only by data flow analysis
            // B1 -> B2 -> B3, B1 -> B3, B3.From = B1 & B2, B3.Input = flag, B1.Output & B1.Def = flag, B2.Output & B2.Def = flag => flag = φ
            //var result = decompiler.Decompile("TestLoop"); //bug: the generated expression is wrong at [v4 ++ += 2]
            //TODO: v4++ shouldn't be kept in registers, just pend to expList and leave v4 in register
            //Maybe block hiding is a bad idea
            //For Condition: if first Block can be merged (Propagation) into 1 statement, then it can be the condition, otherwise no condition.
            //Should be able to determine whether a slot is not used anymore using data flow (Dead) so can perform propagation
            //Add Block.LastRead LastWrite ?
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
            var path = "..\\..\\..\\Res\\Initialize.tjs.comp";
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

        ////DO NOT WORK
        //[TestMethod]
        //public void TestCompileTjs()
        //{
        //    var path = "..\\..\\..\\Res\\Initialize.tjs";
        //    Tjs.mStorage = null;
        //    Tjs.Initialize();
        //    Tjs scriptEngine = new Tjs();
        //    Compiler c = new Compiler(scriptEngine);
        //    using (var fs = File.Create("out.tjsbin"))
        //    {
        //        BinaryStream bs = new TjsBinaryStream(fs);
        //        c.Compile(File.ReadAllText(path), false, false, bs);
        //    }
        //}
    }
}
