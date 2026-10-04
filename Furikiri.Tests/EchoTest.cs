using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Furikiri.AST;
using Furikiri.AST.Expressions;
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
    [DoNotParallelize]
    public class EchoTest
    {
        [TestInitialize]
        public void UseLegacyNamesForExistingSemanticAssertions()
        {
            // 既有语义断言使用旧快照；专门的命名测试同时覆盖新默认和兼容模式。
            Config.UseLegacyRegisterVariableNames = true;
            Config.UseInferredVariableNames = true;
            Config.OpeningBraceOnNewLine = true;
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
        public void ExpressionEffectsUseOneConsistentClassification()
        {
            var value = new IdentifierExpression("value");
            var other = new IdentifierExpression("other");
            var pureComparison = new BinaryExpression(
                value, other, BinaryOp.Equal);
            Assert.IsFalse(
                ExpressionEffectAnalysis.HasObservableEffect(pureComparison));
            Assert.IsTrue(ExpressionEffectAnalysis.IsLikelyPure(pureComparison));
            Assert.IsTrue(
                ExpressionEffectAnalysis.IsProvablySideEffectFree(pureComparison));

            var invocation = new InvokeExpression("probe");
            var callComparison = new BinaryExpression(
                invocation, new ConstantExpression(new TjsInt(0)), BinaryOp.Equal);
            Assert.IsTrue(
                ExpressionEffectAnalysis.HasObservableEffect(callComparison));
            Assert.IsFalse(ExpressionEffectAnalysis.IsLikelyPure(callComparison));
            Assert.IsFalse(
                ExpressionEffectAnalysis.IsProvablySideEffectFree(callComparison));

            var effects = new List<Expression>();
            ExpressionEffectAnalysis.CollectObservableEffects(
                callComparison, effects);
            Assert.HasCount(1, effects);
            Assert.AreSame(invocation, effects[0]);

            var swap = new BinaryExpression(
                new IdentifierExpression("left"),
                new IdentifierExpression("right"), BinaryOp.Swap);
            var assignment = new BinaryExpression(
                new IdentifierExpression("target"),
                new ConstantExpression(new TjsInt(1)), BinaryOp.Assign);
            var increment = new UnaryExpression(
                new IdentifierExpression("counter"), UnaryOp.Inc);
            var evaluation = new UnaryExpression(
                new IdentifierExpression("sourceText"), UnaryOp.Eval);
            var deletion = new DeleteExpression("member")
            {
                Instance = new IdentifierExpression("owner")
            };
            foreach (var mutation in new Expression[]
                     { swap, assignment, increment, evaluation, deletion })
            {
                Assert.IsTrue(
                    ExpressionEffectAnalysis.HasObservableEffect(mutation));
                Assert.IsFalse(ExpressionEffectAnalysis.IsLikelyPure(mutation));
                Assert.IsFalse(
                    ExpressionEffectAnalysis.IsProvablySideEffectFree(mutation));
            }
        }

        [TestMethod]
        public void StructuredConditionalNormalizationRunsBeforeWriting()
        {
            var loopTransfers = new BlockStatement();
            loopTransfers.Statements.Add(new IfStatement(
                new IdentifierExpression("first"), new ContinueStatement(), null));
            loopTransfers.Statements.Add(new IfStatement(
                new IdentifierExpression("second"), new ContinueStatement(), null));
            loopTransfers.Statements.Add(new IfStatement(
                new IdentifierExpression("third"), new ContinueStatement(), null));

            new StructuredAstNormalizationPass().Process(null, loopTransfers);

            Assert.HasCount(1, loopTransfers.Statements,
                "连续的同类循环转移应在 AST 阶段收敛成一个条件");
            var mergedTransfer = (IfStatement)loopTransfers.Statements[0];
            Assert.IsInstanceOfType<BinaryExpression>(mergedTransfer.Condition);
            Assert.AreEqual(BinaryOp.LogicOr,
                ((BinaryExpression)mergedTransfer.Condition).Op);
            Assert.IsInstanceOfType<ContinueStatement>(mergedTransfer.Then);

            var sideEffectfulTransfers = new BlockStatement();
            sideEffectfulTransfers.Statements.Add(new IfStatement(
                new InvokeExpression("probe"), new BreakStatement(), null));
            sideEffectfulTransfers.Statements.Add(new IfStatement(
                new IdentifierExpression("stop"), new BreakStatement(), null));
            new StructuredAstNormalizationPass().Process(
                null, sideEffectfulTransfers);
            Assert.HasCount(2, sideEffectfulTransfers.Statements,
                "可能带副作用的条件不能由规范化阶段重新组合求值");

            var selector = new IdentifierExpression("selector");
            var selectedGuard = new IfStatement(
                new BinaryExpression(
                    new IdentifierExpression("selector"),
                    new IdentifierExpression("fallback"), BinaryOp.LogicOr),
                new BlockStatement
                {
                    Statements =
                    {
                        new ExpressionStatement(new InvokeExpression("payload"))
                    }
                },
                null);
            var selectorBlock = new BlockStatement();
            selectorBlock.Statements.Add(
                new ExpressionStatement(new ConditionExpression(selector)));
            selectorBlock.Statements.Add(selectedGuard);

            new StructuredAstNormalizationPass().Process(null, selectorBlock);

            Assert.HasCount(1, selectorBlock.Statements,
                "被后续 OR 条件吸收的裸选择器不应留给写出器处理");
            Assert.AreSame(selectedGuard, selectorBlock.Statements[0]);

            var firstPayload = new BlockStatement();
            firstPayload.Statements.Add(
                new ExpressionStatement(new InvokeExpression("firstPayload")));
            var sharedPayload = new BlockStatement();
            sharedPayload.Statements.Add(
                new ExpressionStatement(new InvokeExpression("sharedPayload")));
            var sharedElse = new IfStatement(
                new IdentifierExpression("shared"), sharedPayload, null);
            var nested = new IfStatement(
                new IdentifierExpression("nested"), firstPayload, sharedElse);
            var emptySharedShell = new IfStatement(
                new IdentifierExpression("shared"), new BlockStatement(), null);
            var sharedOuter = new IfStatement(
                new IdentifierExpression("outer"), nested, emptySharedShell);
            var sharedBlock = new BlockStatement();
            sharedBlock.Statements.Add(sharedOuter);

            new StructuredAstNormalizationPass().Process(null, sharedBlock);

            Assert.AreSame(firstPayload, sharedOuter.Then);
            Assert.AreSame(sharedElse, sharedOuter.Else);
            Assert.IsTrue(sharedElse.IsElseIf);
            Assert.AreEqual(BinaryOp.LogicAnd,
                ((BinaryExpression)sharedOuter.Condition).Op,
                "共享 else 空壳应在 AST 阶段折叠到外层条件");

            var continuePayload = new BlockStatement();
            continuePayload.Statements.Add(
                new ExpressionStatement(new InvokeExpression("normalBody")));
            var continueInner = new IfStatement(
                new IdentifierExpression("innerSkip"),
                new ContinueStatement(), continuePayload);
            var continueOuter = new IfStatement(
                new IdentifierExpression("outerSkip"),
                new BlockStatement(), continueInner);
            var continueBlock = new BlockStatement();
            continueBlock.Statements.Add(continueOuter);

            new StructuredAstNormalizationPass().Process(null, continueBlock);

            Assert.AreEqual(BinaryOp.LogicOr,
                ((BinaryExpression)continueOuter.Condition).Op);
            Assert.IsInstanceOfType<ContinueStatement>(continueOuter.Then);
            Assert.AreSame(continuePayload, continueOuter.Else,
                "循环 continue 外壳应转成普通 if/else AST，而非写出时替换文本");

            var nestedThenPayload = new BlockStatement();
            nestedThenPayload.Statements.Add(
                new ExpressionStatement(new InvokeExpression("nestedPayload")));
            var nestedThen = new IfStatement(
                new InvokeExpression("secondCondition"), nestedThenPayload, null);
            var nestedThenOuter = new IfStatement(
                new InvokeExpression("firstCondition"),
                new BlockStatement { Statements = { nestedThen } }, null);
            var nestedThenBlock = new BlockStatement();
            nestedThenBlock.Statements.Add(nestedThenOuter);

            new StructuredAstNormalizationPass().Process(null, nestedThenBlock);

            Assert.AreEqual(BinaryOp.LogicAnd,
                ((BinaryExpression)nestedThenOuter.Condition).Op);
            Assert.AreSame(nestedThenPayload, nestedThenOuter.Then,
                "单一内层条件应折叠为保持求值顺序的短路 AND");
        }

        [TestMethod]
        public void EqualitySwitchNormalizationRequiresAStableDispatch()
        {
            BlockStatement Arm(int value)
            {
                var block = new BlockStatement();
                block.Statements.Add(new ExpressionStatement(
                    new InvokeExpression("case" + value)));
                block.Statements.Add(new ContinueStatement());
                return block;
            }

            IfStatement Case(int value, Statement then, Statement next)
            {
                var selector = new LocalExpression(new Variable(3))
                {
                    CachedTemporarySlot = 1,
                    CachedEvaluationId = 10
                };
                return new IfStatement(
                    new BinaryExpression(
                        selector,
                        new ConstantExpression(new TjsInt(value)),
                        BinaryOp.Equal),
                    then,
                    next)
                {
                    IsElseIf = next is IfStatement,
                    IsEqualityDispatch = true
                };
            }

            var fallback = Arm(-1);
            var third = Case(3, Arm(3), fallback);
            var second = Case(2, Arm(2), third);
            var first = Case(1, Arm(1), second);

            Assert.IsTrue(EqualitySwitchNormalizer.TryNormalize(
                first, true, out var normalized));
            Assert.HasCount(3, normalized.Cases);
            Assert.IsInstanceOfType<BreakStatement>(
                normalized.Cases[0].Body.Statements[^1],
                "循环尾 case 的 continue 应恢复为 switch break");
            Assert.IsInstanceOfType<BreakStatement>(
                normalized.Default.Statements[^1]);

            var noDefaultThird = Case(3, Arm(3), null);
            var noDefaultSecond = Case(2, Arm(2), noDefaultThird);
            var noDefaultFirst = Case(1, Arm(1), noDefaultSecond);
            foreach (var conditional in new[]
                     {
                         noDefaultFirst, noDefaultSecond, noDefaultThird
                     })
            {
                // switch break 与“未命中后继续”会汇入同一个 CFG 块。没有实际
                // default 正文时，该可达性不能作为源码贯穿的证据。
                conditional.FallsThroughToEqualityDispatchDefault = true;
                conditional.EqualityDispatchEndsAtLoopLatch = true;
            }

            Assert.IsTrue(EqualitySwitchNormalizer.TryNormalize(
                noDefaultFirst, false, out var noDefaultSwitch));
            Assert.IsNull(noDefaultSwitch.Default);
            Assert.IsTrue(noDefaultSwitch.Cases.All(@case =>
                    @case.Body.Statements[^1] is BreakStatement),
                "没有 default 正文时，每个非终止 case 都必须保留 switch break");

            var loopBreakArm = new BlockStatement();
            loopBreakArm.Statements.Add(new BreakStatement());
            var unsafeChain = Case(1, loopBreakArm,
                Case(2, Arm(2), Case(3, Arm(3), Arm(-1))));
            Assert.IsFalse(EqualitySwitchNormalizer.TryNormalize(
                    unsafeChain, true, out _),
                "原 if 分支中的循环 break 不能改成只退出 switch 的 break");

            var declaration = new BinaryExpression(
                new LocalExpression(new Variable(4)),
                new ConstantExpression(new TjsInt(1)), BinaryOp.Assign)
            {
                IsDeclaration = true
            };
            var scopedFallback = new BlockStatement();
            scopedFallback.Statements.Add(new ExpressionStatement(declaration));
            var returningThird = Case(3,
                new BlockStatement
                {
                    Statements = new List<IAstNode>
                    {
                        new ReturnExpression(new ConstantExpression(new TjsInt(3)))
                    }
                }, scopedFallback);
            var returningSecond = Case(2,
                new BlockStatement
                {
                    Statements = new List<IAstNode>
                    {
                        new ReturnExpression(new ConstantExpression(new TjsInt(2)))
                    }
                }, returningThird);
            var returningFirst = Case(1,
                new BlockStatement
                {
                    Statements = new List<IAstNode>
                    {
                        new ReturnExpression(new ConstantExpression(new TjsInt(1)))
                    }
                }, returningSecond);
            Assert.IsFalse(EqualitySwitchNormalizer.TryNormalize(
                    returningFirst, false, out _),
                "所有 case 都终止时，带新声明的未命中分支可能是 switch 后续代码，不能压入 default");

            var nestedDispatch = Case(1, Arm(1), Case(2, Arm(2), Arm(-1)));
            var outer = new IfStatement(
                new IdentifierExpression("enabled"), Arm(0), nestedDispatch);
            var outerBlock = new BlockStatement();
            outerBlock.Statements.Add(outer);
            new StructuredAstNormalizationPass().Process(null, outerBlock);
            Assert.IsInstanceOfType<SwitchStatement>(outer.Else,
                "直接作为 else 子节点的 equality 分派也必须取得父引用并替换为 switch");
        }

        [TestMethod]
        public void EqualityDispatchRecognizesSharedCaseBodyBehindLabelJump()
        {
            BinaryExpression Comparison(string label) => new BinaryExpression(
                new IdentifierExpression("kind"),
                new ConstantExpression(new TjsString(label)), BinaryOp.Equal);

            var previous = new Block(0) { Id = 0, End = 0 };
            var labelJump = new Block(1) { Id = 1, End = 1 };
            var current = new Block(2) { Id = 2, End = 2 };
            var sharedBody = new Block(3) { Id = 3, End = 3 };
            var fallback = new Block(4) { Id = 4, End = 4 };
            previous.To.AddRange(new[] { labelJump, current });
            labelJump.From.Add(previous);
            current.From.Add(previous);
            labelJump.To.Add(sharedBody);
            sharedBody.From.Add(labelJump);
            current.To.AddRange(new[] { sharedBody, fallback });
            sharedBody.From.Add(current);
            fallback.From.Add(current);
            previous.Statements = new List<IAstNode>
            {
                new ConditionExpression(Comparison("Integer"))
                {
                    JumpTo = labelJump.Start,
                    ElseTo = current.Start
                }
            };
            labelJump.Statements = new List<IAstNode>
                { new GotoExpression { JumpTo = sharedBody.Start } };
            current.Statements = new List<IAstNode>
            {
                new ConditionExpression(Comparison("Real"))
                {
                    JumpTo = sharedBody.Start,
                    ElseTo = fallback.Start
                }
            };
            sharedBody.Statements = new List<IAstNode>
                { new ReturnExpression(new IdentifierExpression("number")) };
            fallback.Statements = new List<IAstNode>
                { new ReturnExpression() };

            Assert.IsTrue(EqualityDispatchAnalyzer.IsSharedCaseBody(
                    current, sharedBody),
                "前一标签经纯跳板进入的正文应保留给完整相等分派");
            Assert.IsFalse(EqualityDispatchAnalyzer.IsSharedCaseBody(
                    current, fallback),
                "当前比较独占的默认出口不能误判成共享 case 正文");
            Assert.IsTrue(previous.Statements.IsCondition());
            Assert.IsTrue(current.Statements.IsCondition());
            Assert.IsFalse(sharedBody.Hidden,
                "共享正文识别必须保持 CFG 可见性不变");
        }

        [TestMethod]
        public void EqualitySwitchUsesDefiningInstructionIdentityForTypeOf()
        {
            BlockStatement Body(string name)
            {
                var body = new BlockStatement();
                body.Statements.Add(new ExpressionStatement(
                    new InvokeExpression(name)));
                return body;
            }

            IfStatement TypeCase(
                string label, int evaluationId, Statement next)
            {
                var selector = new UnaryExpression(
                    new IdentifierExpression("value"), UnaryOp.TypeOf)
                {
                    CachedTemporarySlot = 1,
                    CachedEvaluationId = evaluationId
                };
                return new IfStatement(
                    new BinaryExpression(
                        selector,
                        new ConstantExpression(new TjsString(label)),
                        BinaryOp.Equal),
                    Body("case" + label), next)
                {
                    IsEqualityDispatch = true,
                    IsElseIf = next is IfStatement
                };
            }

            // 工作表重新处理后可以产生三个不同 AST 对象，但它们都来自同一条
            // TYPEOF 指令，因此 switch 仍只求值一次。
            var shared = TypeCase("Integer", 12,
                TypeCase("String", 12,
                    TypeCase("Object", 12, Body("fallback"))));
            Assert.IsTrue(EqualitySwitchNormalizer.TryNormalize(
                shared, false, out var normalized));
            Assert.HasCount(3, normalized.Cases);
            Assert.AreEqual(12,
                normalized.Expression.CachedEvaluationId);

            // 临时槽虽然相同，两条独立 TYPEOF 指令的身份不同，不能擅自缩成
            // switch 的一次求值。
            var independent = TypeCase("Integer", 20,
                TypeCase("String", 21,
                    TypeCase("Object", 22, Body("fallback"))));
            Assert.IsFalse(EqualitySwitchNormalizer.TryNormalize(
                independent, false, out _));

            IfStatement ComputedCase(
                int label, int evaluationId, Statement next)
            {
                var selector = new BinaryExpression(
                    new IdentifierExpression("value"),
                    new ConstantExpression(new TjsInt(3)), BinaryOp.Mod)
                {
                    CachedTemporarySlot = 2,
                    CachedEvaluationId = evaluationId
                };
                return new IfStatement(
                    new BinaryExpression(
                        selector,
                        new ConstantExpression(new TjsInt(label)),
                        BinaryOp.Equal),
                    Body("case" + label), next)
                {
                    IsEqualityDispatch = true,
                    IsElseIf = next is IfStatement
                };
            }

            // 两个显式标签加 default 也足以构成 switch；定义身份已经证明
            // `value % 3` 只求值一次，不需要再把安全范围限定为 typeof。
            var computed = ComputedCase(0, 30,
                ComputedCase(1, 30, Body("fallback")));
            Assert.IsTrue(EqualitySwitchNormalizer.TryNormalize(
                computed, false, out var computedSwitch));
            Assert.HasCount(2, computedSwitch.Cases);
            Assert.AreEqual(BinaryOp.Mod,
                ((BinaryExpression)computedSwitch.Expression).Op);

            var repeatedComputation = ComputedCase(0, 40,
                ComputedCase(1, 41, Body("fallback")));
            Assert.IsFalse(EqualitySwitchNormalizer.TryNormalize(
                    repeatedComputation, false, out _),
                "结构相同但分别求值的计算式不能合并为 switch");

            IfStatement DynamicCase(
                string member, Statement body, Statement next)
            {
                var selector = new LocalExpression(new Variable(5))
                {
                    CachedTemporarySlot = 3,
                    CachedEvaluationId = 50
                };
                var label = new IdentifierExpression(member)
                {
                    Instance = new IdentifierExpression("labels")
                };
                return new IfStatement(
                    new BinaryExpression(selector, label, BinaryOp.Equal),
                    body, next)
                {
                    IsEqualityDispatch = true,
                    IsElseIf = next is IfStatement
                };
            }

            var dynamicLabels = DynamicCase("first", Body("first"),
                DynamicCase("second", Body("second"),
                    DynamicCase("third", Body("third"), Body("fallback"))));
            Assert.IsTrue(EqualitySwitchNormalizer.TryNormalize(
                dynamicLabels, false, out var dynamicSwitch));
            Assert.HasCount(3, dynamicSwitch.Cases);
            Assert.AreEqual("first",
                ((IdentifierExpression)dynamicSwitch.Cases[0].Labels[0]).Name,
                "case 标签表达式应保持原顺序和成员访问结构");
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
        public void PathPredicateDoesNotMergeSameMemberNameOnDifferentInstances()
        {
            var condition = new IdentifierExpression("gate");
            var leftValue = new IdentifierExpression("value")
            {
                Instance = new IdentifierExpression("left")
            };
            var rightValue = new IdentifierExpression("value")
            {
                Instance = new IdentifierExpression("right")
            };

            var merged = PathPredicate.Combine(
                condition,
                PathPredicate.From(leftValue),
                PathPredicate.From(rightValue));

            Assert.IsNotNull(merged);
            Assert.IsInstanceOfType<BinaryExpression>(merged.Expression,
                "不同实例上的同名成员不能被路径吸收规则误判为同一个条件");
            Assert.AreEqual(BinaryOp.LogicOr,
                ((BinaryExpression)merged.Expression).Op);
        }

        [TestMethod]
        public void IfRegionAnalyzerUsesReachableArmAsNormalContinuation()
        {
            var condition = new Block(0) { Id = 0, End = 0 };
            var body = new Block(1) { Id = 1, End = 1 };
            var continuation = new Block(2) { Id = 2, End = 2 };
            condition.To.Add(body);
            condition.To.Add(continuation);
            body.To.Add(continuation);

            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { condition, body, continuation });
            var graph = new ControlFlowGraphAnalysis(context);
            var plan = IfRegionAnalyzer.Analyze(
                graph,
                condition,
                body,
                continuation,
                null,
                null,
                _ => false,
                null);

            Assert.IsTrue(plan.ThenCanReachElse);
            Assert.IsFalse(plan.ElseCanReachThen);
            Assert.AreSame(continuation, plan.NormalContinuation,
                "一条分支落入另一条分支时，被落入的一侧应作为 if 后的公共继续点");
            CollectionAssert.AreEqual(new[] { body }, plan.ThenBlocks.ToArray());
            Assert.AreEqual(0, plan.ElseBlocks.Count);
            Assert.AreEqual(IfRegionExitKind.FallsThrough, plan.ThenExit);
            Assert.AreEqual(IfRegionExitKind.FallsThrough, plan.ElseExit);
        }

        [TestMethod]
        public void IfRegionAnalyzerTreatsExcludedLoopHeaderAsReachableTargetOnly()
        {
            var condition = new Block(0) { Id = 0, End = 0 };
            var body = new Block(1) { Id = 1, End = 1 };
            var loopHeader = new Block(2) { Id = 2, End = 2 };
            condition.To.Add(body);
            condition.To.Add(loopHeader);
            body.To.Add(loopHeader);
            loopHeader.To.Add(condition);

            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { condition, body, loopHeader });
            var graph = new ControlFlowGraphAnalysis(context);

            Assert.IsTrue(graph.CanReachWithoutEntering(
                body, loopHeader, loopHeader),
                "循环头作为查询目标时，抵达它的回边仍应算可达");
            Assert.IsFalse(graph.CanReachWithoutEntering(
                loopHeader, body, loopHeader),
                "从被排除的循环头出发不能穿入下一轮正文");

            var plan = IfRegionAnalyzer.Analyze(
                graph,
                condition,
                body,
                loopHeader,
                null,
                loopHeader,
                _ => false,
                candidate => candidate == loopHeader,
                null);

            Assert.AreSame(loopHeader, plan.NormalContinuation);
            CollectionAssert.AreEqual(
                new[] { body }, plan.ThenBlocks.ToArray());
            Assert.AreEqual(0, plan.ElseBlocks.Count);
        }

        [TestMethod]
        public void IfRegionAnalyzerFindsCurrentIterationSharedTail()
        {
            var header = new Block(0) { Id = 0, End = 0 };
            var thenBody = new Block(1) { Id = 1, End = 1 };
            var elseBody = new Block(2) { Id = 2, End = 2 };
            var continueBlock = new Block(3) { Id = 3, End = 3 };
            var sharedTail = new Block(4) { Id = 4, End = 4 };
            header.To.Add(thenBody);
            header.To.Add(elseBody);
            thenBody.To.Add(sharedTail);
            elseBody.To.Add(continueBlock);
            elseBody.To.Add(sharedTail);
            continueBlock.To.Add(header);
            sharedTail.To.Add(header);

            var context = new DecompileContext();
            context.Blocks.AddRange(new[]
            {
                header, thenBody, elseBody, continueBlock, sharedTail
            });
            var graph = new ControlFlowGraphAnalysis(context);
            var plan = IfRegionAnalyzer.Analyze(
                graph, header, thenBody, elseBody,
                // 模拟图论后支配点沿回边落到下一轮的 else 入口。
                elseBody, header, _ => false,
                candidate => candidate == header || candidate == continueBlock);

            Assert.IsTrue(plan.UsesSameIterationContinuation);
            Assert.AreSame(sharedTail, plan.NormalContinuation);
            CollectionAssert.AreEqual(
                new[] { thenBody }, plan.ThenBlocks.ToArray());
            CollectionAssert.AreEquivalent(
                new[] { elseBody, continueBlock }, plan.ElseBlocks.ToArray());
        }

        [TestMethod]
        public void MutationScopeRejectsChangesOutsideOwnedRegion()
        {
            var owned = new Block(0)
            {
                Id = 0, End = 0, Statements = new List<IAstNode>()
            };
            var outside = new Block(1)
            {
                Id = 1, End = 1, Statements = new List<IAstNode>()
            };
            outside.Statements.Add(new IdentifierExpression("shared"));

            using (var mutation = new ControlFlowMutationScope(
                       new[] { owned, outside }))
            {
                outside.Hidden = true;
                Assert.IsTrue(mutation.HasChangesOutside(
                    new HashSet<Block> { owned }));
            }

            Assert.IsFalse(outside.Hidden,
                "越界候选未提交时必须恢复公共块可见性");
            Assert.AreEqual(1, outside.Statements.Count);
        }

        [TestMethod]
        public void IfRegionAnalyzerDoesNotTreatSyntheticExitAsFallthrough()
        {
            var condition = new Block(0) { Id = 0, End = 0 };
            var returning = new Block(1) { Id = 1, End = 1 };
            var normalBody = new Block(2) { Id = 2, End = 2 };
            var exit = new Block(3) { Id = 3, End = 3 };
            condition.To.Add(returning);
            condition.To.Add(normalBody);
            // return/throw 在分析图中仍可能连到统一出口；该边不代表源码落空。
            returning.To.Add(exit);
            normalBody.To.Add(exit);

            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { condition, returning, normalBody, exit });
            var graph = new ControlFlowGraphAnalysis(context);
            var plan = IfRegionAnalyzer.Analyze(
                graph,
                condition,
                returning,
                normalBody,
                exit,
                null,
                block => block == returning,
                null,
                block => block == returning
                    ? IfRegionExitKind.Return
                    : IfRegionExitKind.Unknown);

            CollectionAssert.AreEqual(new[] { returning }, plan.ThenBlocks.ToArray());
            Assert.AreEqual(0, plan.ElseBlocks.Count,
                "正常正文被选为公共继续点后不应归入 else 分支");
            Assert.AreEqual(IfRegionExitKind.Return, plan.ThenExit);
            Assert.AreEqual(IfRegionExitKind.FallsThrough, plan.ElseExit);
        }

        [TestMethod]
        public void IfRegionMaterializationPlanFreezesOwnedBlocksWithoutMutation()
        {
            var thenEntry = new Block(10) { Id = 0, End = 10 };
            var thenTail = new Block(20) { Id = 1, End = 20 };
            var elseEntry = new Block(30) { Id = 2, End = 30 };
            var continuation = new Block(40) { Id = 3, End = 40 };
            var outsider = new Block(50) { Id = 4, End = 50 };
            var thenBlocks = new List<Block> { thenEntry, thenTail };
            var elseBlocks = new List<Block> { elseEntry };

            var plan = IfRegionMaterializationAnalyzer.Analyze(
                thenEntry, elseEntry, continuation, thenBlocks, elseBlocks);

            Assert.AreEqual(IfRegionMaterializationKind.MultiBlock, plan.Kind);
            CollectionAssert.AreEqual(
                new[] { thenEntry, thenTail }, plan.ThenBlocks.ToArray());
            CollectionAssert.AreEqual(
                new[] { elseEntry }, plan.ElseBlocks.ToArray());
            Assert.IsFalse(thenEntry.Hidden);
            Assert.IsFalse(thenTail.Hidden);
            Assert.IsFalse(elseEntry.Hidden,
                "只读所有权分析不能提前隐藏任何候选块");

            // 后续修改调用方列表不能扩大已经确定的区域所有权。
            thenBlocks.Add(outsider);
            thenEntry.Hidden = true;
            thenTail.Hidden = true;
            var layout = plan.ResolveVisibleLayout();
            Assert.IsTrue(layout.InvertCondition);
            CollectionAssert.AreEqual(
                new[] { elseEntry }, layout.ThenBlocks.ToArray());
            Assert.AreEqual(0, layout.ElseBlocks.Count);
            Assert.IsFalse(plan.NestedCandidates.Contains(outsider));
        }

        [TestMethod]
        public void IfRegionMaterializationPlanSeparatesSingleAndMultiBlockPaths()
        {
            var thenEntry = new Block(10) { Id = 0, End = 10 };
            var elseEntry = new Block(20) { Id = 1, End = 20 };
            var continuation = new Block(30) { Id = 2, End = 30 };

            var thenSingle = IfRegionMaterializationAnalyzer.Analyze(
                thenEntry, continuation, continuation,
                new[] { thenEntry }, Array.Empty<Block>());
            Assert.AreEqual(
                IfRegionMaterializationKind.ThenSingleArm, thenSingle.Kind);

            var unrelatedSingleBlocks = IfRegionMaterializationAnalyzer.Analyze(
                thenEntry, elseEntry, continuation,
                new[] { thenEntry }, new[] { elseEntry });
            Assert.AreEqual(IfRegionMaterializationKind.None,
                unrelatedSingleBlocks.Kind,
                "两个单块分支应继续交给 break/continue 和 else-if 专用路径");
        }

        [TestMethod]
        public void DecisionDagAnalyzerBuildsPlanWithoutMutatingBlocks()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var inner = new Block(1) { Id = 1, End = 1 };
            var body = new Block(2) { Id = 2, End = 2 };
            var continuation = new Block(3) { Id = 3, End = 3 };
            root.To.AddRange(new[] { inner, continuation });
            inner.To.AddRange(new[] { body, continuation });
            body.To.Add(continuation);
            root.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("first"))
                    { JumpTo = inner.Start, ElseTo = continuation.Start }
            };
            inner.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("second"))
                    { JumpTo = body.Start, ElseTo = continuation.Start }
            };
            body.Statements = new List<IAstNode>
            {
                new InvokeExpression("payload"),
                new GotoExpression { JumpTo = continuation.Start }
            };
            continuation.Statements = new List<IAstNode>();

            void SetPostDominators(Block block, params int[] ids)
            {
                block.PostDominator = new System.Collections.BitArray(4);
                foreach (var id in ids)
                {
                    block.PostDominator[id] = true;
                }
            }
            SetPostDominators(root, 0, 3);
            SetPostDominators(inner, 1, 3);
            SetPostDominators(body, 2, 3);
            SetPostDominators(continuation, 3);

            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { root, inner, body, continuation });
            context.UpdateBlockTable();
            var graph = new ControlFlowGraphAnalysis(context);

            Assert.IsTrue(DecisionDagAnalyzer.TryAnalyzePayload(
                context, graph, root, out var plan));
            Assert.AreSame(body, plan.Body);
            Assert.AreSame(continuation, plan.Continuation);
            Assert.IsInstanceOfType<BinaryExpression>(plan.Predicate);
            Assert.AreEqual(BinaryOp.LogicAnd,
                ((BinaryExpression)plan.Predicate).Op);
            Assert.IsFalse(root.Hidden);
            Assert.IsFalse(inner.Hidden,
                "只读分析成功后也不能提前隐藏内部条件块");
            Assert.IsFalse(body.Hidden);

            var logic = DecisionDagMaterializer.Materialize(plan);
            Assert.IsFalse(root.Hidden, "根块必须继续承载生成后的区域语句");
            Assert.IsTrue(inner.Hidden, "采用计划后才提交内部条件块所有权");
            Assert.IsFalse(body.Hidden, "载荷正文由 IfLogic 引用，不能作为控制跳板隐藏");
            Assert.IsFalse(body.Statements.Any(statement => statement is GotoExpression),
                "区域内部通往公共继续点的跳转应在统一提交时删除");
            Assert.AreSame(root, logic.ConditionBlock);
            Assert.AreSame(body, logic.Then.Blocks.Single());
            Assert.AreSame(continuation, logic.PostDominator);
        }

        [TestMethod]
        public void TerminalGuardPlannerBuildsExclusiveArmPlanWithoutMutation()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var continuation = new Block(1) { Id = 1, End = 1 };
            var returning = new Block(2) { Id = 2, End = 2 };
            root.To.AddRange(new[] { continuation, returning });
            continuation.From.Add(root);
            returning.From.Add(root);
            var condition = new ConditionExpression(
                new IdentifierExpression("continueWork"))
            {
                JumpTo = continuation.Start,
                ElseTo = returning.Start
            };
            root.Statements = new List<IAstNode> { condition };
            continuation.Statements = new List<IAstNode>
                { new InvokeExpression("work") };
            returning.Statements = new List<IAstNode>
                { new ReturnExpression() };

            Assert.IsTrue(TerminalGuardPlanner.TryAnalyze(root, out var plan));
            Assert.AreSame(root, plan.Root);
            Assert.AreSame(returning, plan.Terminal);
            Assert.AreSame(continuation, plan.Continuation);
            Assert.AreEqual(TerminalGuardKind.Return, plan.Kind);
            Assert.IsInstanceOfType<UnaryExpression>(plan.Predicate,
                "终止臂位于条件假边时，计划应只记录取反谓词");
            Assert.AreSame(condition, root.Statements.Single(),
                "只读分析不能提前替换原条件");
            Assert.IsFalse(root.Hidden);
            Assert.IsFalse(returning.Hidden);

            var logic = TerminalGuardMaterializer.Materialize(plan);
            Assert.AreSame(root, logic.ConditionBlock);
            Assert.AreSame(returning, logic.Then.Blocks.Single());
            Assert.AreSame(continuation, logic.PostDominator);
            Assert.AreSame(condition, root.Statements.Single(),
                "物化器只构造区域，提交仍由控制流阶段一次完成");
        }

        [TestMethod]
        public void TerminalConditionPlannerOrdersPureGuardsWithoutMutation()
        {
            (Block Root, Block Returning, Block Body) Guard(int start, string name)
            {
                var root = new Block(start) { Id = start, End = start };
                var returning = new Block(start + 1)
                    { Id = start + 1, End = start + 1 };
                var body = new Block(start + 2)
                    { Id = start + 2, End = start + 2 };
                root.To.AddRange(new[] { returning, body });
                root.Statements = new List<IAstNode>
                {
                    new ConditionExpression(new IdentifierExpression(name))
                    {
                        JumpTo = returning.Start,
                        ElseTo = body.Start
                    }
                };
                returning.Statements = new List<IAstNode>
                    { new ReturnExpression() };
                body.Statements = new List<IAstNode>
                    { new InvokeExpression("body" + name) };
                return (root, returning, body);
            }

            var later = Guard(20, "later");
            var earlier = Guard(10, "earlier");
            var context = new DecompileContext();
            context.Blocks.AddRange(new[]
            {
                later.Root, later.Returning, later.Body,
                earlier.Root, earlier.Returning, earlier.Body
            });

            var plan = TerminalConditionChainPlanner.Create(
                context, TerminalConditionChainStage.PureCondition);

            CollectionAssert.AreEqual(
                new[] { earlier.Root, later.Root },
                plan.Select(candidate => candidate.Root).ToArray(),
                "同阶段候选必须按字节码入口稳定调度");
            Assert.IsTrue(plan.All(candidate =>
                candidate.Stage == TerminalConditionChainStage.PureCondition));
            Assert.IsTrue(context.Blocks.All(block => !block.Hidden),
                "只读调度计划不能提前取得任何基本块的所有权");
        }

        [TestMethod]
        public void TerminalConditionPlannerSeparatesEntryPayloadGuards()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var returning = new Block(1) { Id = 1, End = 1 };
            var body = new Block(2) { Id = 2, End = 2 };
            root.To.AddRange(new[] { returning, body });
            var initializer = new BinaryExpression(
                new LocalExpression(new Variable(3)),
                new InvokeExpression("prepare"), BinaryOp.Assign);
            root.Statements = new List<IAstNode>
            {
                initializer,
                new ConditionExpression(new IdentifierExpression("reject"))
                {
                    JumpTo = returning.Start,
                    ElseTo = body.Start
                }
            };
            returning.Statements = new List<IAstNode>
                { new ReturnExpression() };
            body.Statements = new List<IAstNode>
                { new InvokeExpression("normalBody") };
            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { root, returning, body });

            Assert.IsEmpty(TerminalConditionChainPlanner.Create(
                context, TerminalConditionChainStage.PureCondition),
                "带入口载荷的块不能被纯条件阶段提前消费");
            var plan = TerminalConditionChainPlanner.Create(
                context, TerminalConditionChainStage.EntryPayload);
            Assert.HasCount(1, plan);
            Assert.AreSame(root, plan[0].Root);
            Assert.IsTrue(plan[0].HasPrefixSideEffect,
                "调度计划应冻结入口调用的副作用事实");
            Assert.AreEqual(2, root.Statements.Count);
            Assert.AreSame(initializer, root.Statements[0]);
            Assert.IsFalse(root.Hidden);
        }

        [TestMethod]
        public void TerminalConditionPlannerRejectsCompleteReturnDispatch()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var firstReturn = new Block(1) { Id = 1, End = 1 };
            var secondReturn = new Block(2) { Id = 2, End = 2 };
            root.To.AddRange(new[] { firstReturn, secondReturn });
            root.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("select"))
                {
                    JumpTo = firstReturn.Start,
                    ElseTo = secondReturn.Start
                }
            };
            firstReturn.Statements = new List<IAstNode>
                { new ReturnExpression(new IdentifierExpression("left")) };
            secondReturn.Statements = new List<IAstNode>
                { new ReturnExpression(new IdentifierExpression("right")) };
            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { root, firstReturn, secondReturn });

            Assert.IsEmpty(TerminalConditionChainPlanner.Create(
                context, TerminalConditionChainStage.PureCondition),
                "两侧都 return 的完整分派不属于单出口提前返回守卫");
            Assert.IsFalse(root.Hidden);
            Assert.IsFalse(firstReturn.Hidden);
            Assert.IsFalse(secondReturn.Hidden);
        }

        [TestMethod]
        public void ControlFlowMutationScopeRollsBackRejectedCandidate()
        {
            var originalOperand = new IdentifierExpression("original");
            var condition = new ConditionExpression(originalOperand)
            {
                JumpIf = true,
                JumpTo = 10,
                ElseTo = 20
            };
            originalOperand.Parent = condition;
            var nested = new BlockStatement();
            var nestedCall = new InvokeExpression("nestedPayload");
            var originalParameter = new IdentifierExpression("argument");
            nestedCall.Parameters.Add(originalParameter);
            nested.Statements.Add(nestedCall);
            var block = new Block(0)
            {
                Id = 0,
                End = 0,
                Statements = new List<IAstNode> { condition, nested }
            };
            var originalStatements = block.Statements;
            var originalNestedStatements = nested.Statements;

            using (new ControlFlowMutationScope(new[] { block }))
            {
                block.Hidden = true;
                block.Statements = new List<IAstNode>
                    { new InvokeExpression("partialPayload") };
                nested.Statements.Clear();
                condition.Condition = new IdentifierExpression("changed");
                condition.JumpIf = false;
                condition.JumpTo = 30;
                condition.ElseTo = 40;
                originalOperand.Parent = null;
                nestedCall.MethodName = "changedPayload";
                nestedCall.Parameters.Clear();
                nestedCall.Parameters.Add(new IdentifierExpression("changedArgument"));
            }

            Assert.IsFalse(block.Hidden);
            Assert.AreSame(originalStatements, block.Statements,
                "拒绝候选后应恢复原来的基本块语句列表对象");
            CollectionAssert.AreEqual(
                new IAstNode[] { condition, nested }, block.Statements);
            Assert.AreSame(originalNestedStatements, nested.Statements);
            CollectionAssert.AreEqual(
                new IAstNode[] { nestedCall }, nested.Statements);
            Assert.AreEqual("nestedPayload", nestedCall.MethodName);
            CollectionAssert.AreEqual(
                new Expression[] { originalParameter }, nestedCall.Parameters,
                "嵌套调用的可变参数列表也必须回滚");
            Assert.AreSame(originalOperand, condition.Condition);
            Assert.IsTrue(condition.JumpIf);
            Assert.AreEqual(10, condition.JumpTo);
            Assert.AreEqual(20, condition.ElseTo);
            Assert.AreSame(condition, originalOperand.Parent);
        }

        [TestMethod]
        public void ControlFlowMutationScopeKeepsCommittedCandidate()
        {
            var block = new Block(0)
            {
                Id = 0,
                End = 0,
                Statements = new List<IAstNode>
                    { new IdentifierExpression("before") }
            };
            var replacement = new InvokeExpression("after");

            using (var mutation = new ControlFlowMutationScope(new[] { block }))
            {
                block.Hidden = true;
                block.Statements.Clear();
                block.Statements.Add(replacement);
                mutation.Commit();
            }

            Assert.IsTrue(block.Hidden);
            CollectionAssert.AreEqual(
                new IAstNode[] { replacement }, block.Statements);
        }

        [TestMethod]
        public void DecisionDagAnalyzerRejectsMultiBlockPayloadWithoutMutation()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var firstBody = new Block(1) { Id = 1, End = 1 };
            var secondBody = new Block(2) { Id = 2, End = 2 };
            var continuation = new Block(3) { Id = 3, End = 3 };
            root.To.AddRange(new[] { firstBody, continuation });
            firstBody.To.Add(secondBody);
            secondBody.To.Add(continuation);
            root.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("gate"))
                    { JumpTo = firstBody.Start, ElseTo = continuation.Start }
            };
            firstBody.Statements = new List<IAstNode>
                { new InvokeExpression("firstPayload") };
            secondBody.Statements = new List<IAstNode>
                { new InvokeExpression("secondPayload") };
            continuation.Statements = new List<IAstNode>();
            root.PostDominator = new System.Collections.BitArray(4);
            root.PostDominator[0] = root.PostDominator[3] = true;
            firstBody.PostDominator = new System.Collections.BitArray(4);
            firstBody.PostDominator[1] = firstBody.PostDominator[2] =
                firstBody.PostDominator[3] = true;
            secondBody.PostDominator = new System.Collections.BitArray(4);
            secondBody.PostDominator[2] = secondBody.PostDominator[3] = true;
            continuation.PostDominator = new System.Collections.BitArray(4);
            continuation.PostDominator[3] = true;

            var context = new DecompileContext();
            context.Blocks.AddRange(
                new[] { root, firstBody, secondBody, continuation });
            context.UpdateBlockTable();
            var graph = new ControlFlowGraphAnalysis(context);

            Assert.IsFalse(DecisionDagAnalyzer.TryAnalyzePayload(
                context, graph, root, out _));
            Assert.IsFalse(root.Hidden);
            Assert.IsFalse(firstBody.Hidden);
            Assert.IsFalse(secondBody.Hidden,
                "拒绝多块载荷时不能留下部分区域所有权");
        }

        [TestMethod]
        public void DecisionDagAnalyzerBuildsTwoPayloadPlanWithoutMutation()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var inner = new Block(1) { Id = 1, End = 1 };
            var success = new Block(2) { Id = 2, End = 2 };
            var fallback = new Block(3) { Id = 3, End = 3 };
            var continuation = new Block(4) { Id = 4, End = 4 };
            root.To.AddRange(new[] { inner, fallback });
            inner.From.Add(root);
            fallback.From.Add(root);
            inner.To.AddRange(new[] { success, fallback });
            success.From.Add(inner);
            fallback.From.Add(inner);
            success.To.Add(continuation);
            fallback.To.Add(continuation);
            continuation.From.AddRange(new[] { success, fallback });
            root.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("first"))
                    { JumpTo = inner.Start, ElseTo = fallback.Start }
            };
            inner.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("second"))
                    { JumpTo = success.Start, ElseTo = fallback.Start }
            };
            success.Statements = new List<IAstNode>
            {
                new BinaryExpression(
                    new IdentifierExpression("value"),
                    new IdentifierExpression("precise"), BinaryOp.Assign)
            };
            fallback.Statements = new List<IAstNode>
            {
                new BinaryExpression(
                    new IdentifierExpression("value"),
                    new IdentifierExpression("coarse"), BinaryOp.Assign)
            };
            continuation.Statements = new List<IAstNode>
                { new InvokeExpression("consume") };

            var context = new DecompileContext();
            context.Blocks.AddRange(
                new[] { root, inner, success, fallback, continuation });
            context.UpdateBlockTable();
            var graph = new ControlFlowGraphAnalysis(context);

            Assert.IsTrue(DecisionDagAnalyzer.TryAnalyzeTwoPayloadBranches(
                context, graph, root, out var plan));
            Assert.AreSame(success, plan.TrueTarget);
            Assert.AreSame(fallback, plan.FalseTarget);
            Assert.AreSame(continuation, plan.Continuation);
            Assert.IsInstanceOfType<BinaryExpression>(plan.Predicate);
            Assert.AreEqual(BinaryOp.LogicAnd,
                ((BinaryExpression)plan.Predicate).Op);
            Assert.IsFalse(root.Hidden);
            Assert.IsFalse(inner.Hidden);
            Assert.IsFalse(success.Hidden);
            Assert.IsFalse(fallback.Hidden,
                "双载荷候选的只读分析不能提前取得任何块的所有权");

            var external = new Block(5) { Id = 5, End = 5 };
            external.To.Add(fallback);
            fallback.From.Add(external);
            context.Blocks.Add(external);
            context.UpdateBlockTable();
            graph = new ControlFlowGraphAnalysis(context);
            Assert.IsFalse(DecisionDagAnalyzer.TryAnalyzeTwoPayloadBranches(
                context, graph, root, out _),
                "计划外路径仍使用回退载荷时，当前 DAG 不能取得该叶子所有权");
            Assert.IsFalse(fallback.Hidden,
                "拒绝外部共享叶时不能留下部分可见性修改");
        }

        [TestMethod]
        public void DecisionDagReturnAnalyzerUsesAssignmentConditionWithoutMutation()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var inner = new Block(1) { Id = 1, End = 1 };
            var returning = new Block(2) { Id = 2, End = 2 };
            var body = new Block(3) { Id = 3, End = 3 };
            root.To.AddRange(new[] { inner, body });
            inner.To.AddRange(new[] { returning, body });
            inner.From.Add(root);
            returning.From.Add(inner);
            body.From.AddRange(new[] { root, inner });

            root.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("first"))
                    { JumpTo = inner.Start, ElseTo = body.Start }
            };
            var cachedVariable = new Variable(4);
            var assignment = new BinaryExpression(
                new LocalExpression(cachedVariable),
                new IdentifierExpression("source"),
                BinaryOp.Assign);
            inner.Statements = new List<IAstNode>
            {
                assignment,
                new ConditionExpression(new BinaryExpression(
                    new LocalExpression(cachedVariable),
                    new IdentifierExpression("void"),
                    BinaryOp.Congruent))
                    { JumpTo = returning.Start, ElseTo = body.Start }
            };
            returning.Statements = new List<IAstNode>
            {
                new ReturnExpression(new IdentifierExpression("zero"))
            };
            body.Statements = new List<IAstNode>
                { new InvokeExpression("normalBody") };

            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { root, inner, returning, body });
            context.UpdateBlockTable();
            var graph = new ControlFlowGraphAnalysis(context);

            Assert.IsTrue(DecisionDagAnalyzer.TryAnalyzeReturnGuard(
                context, graph, root, out var plan));
            Assert.AreSame(returning, plan.ReturnTarget);
            Assert.AreSame(body, plan.BodyTarget);
            Assert.AreEqual(2, inner.Statements.Count,
                "只读分析不能删除条件前的赋值");
            Assert.AreSame(assignment, inner.Statements[0]);
            Assert.IsFalse(inner.Hidden);

            bool ContainsAssignment(Expression expression) =>
                expression is BinaryExpression binary &&
                (binary.Op == BinaryOp.Assign ||
                 ContainsAssignment(binary.Left) ||
                 ContainsAssignment(binary.Right)) ||
                expression is UnaryExpression unary &&
                ContainsAssignment(unary.Target);
            Assert.IsTrue(ContainsAssignment(plan.Predicate),
                "计划谓词应使用赋值后的比较，而不修改原基本块");

            var memberAssignment = new BinaryExpression(
                new IdentifierExpression("cachedMember"),
                new IdentifierExpression("source"),
                BinaryOp.Assign);
            inner.Statements = new List<IAstNode>
            {
                memberAssignment,
                new ConditionExpression(new BinaryExpression(
                    new IdentifierExpression("cachedMember"),
                    new IdentifierExpression("void"),
                    BinaryOp.Congruent))
                    { JumpTo = returning.Start, ElseTo = body.Start }
            };
            Assert.IsFalse(DecisionDagAnalyzer.TryAnalyzeReturnGuard(
                    context, graph, root, out _),
                "成员写入后的读取可能触发 getter，不能替换为赋值右值");
            Assert.AreEqual(2, inner.Statements.Count,
                "拒绝成员赋值候选时不能修改原基本块");
        }

        [TestMethod]
        public void DecisionDagReturnAnalyzerRejectsThreeTerminalsWithoutMutation()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var inner = new Block(1) { Id = 1, End = 1 };
            var returning = new Block(2) { Id = 2, End = 2 };
            var firstBody = new Block(3) { Id = 3, End = 3 };
            var secondBody = new Block(4) { Id = 4, End = 4 };
            root.To.AddRange(new[] { inner, firstBody });
            inner.To.AddRange(new[] { returning, secondBody });
            root.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("first"))
                    { JumpTo = inner.Start, ElseTo = firstBody.Start }
            };
            var assignment = new BinaryExpression(
                new IdentifierExpression("cached"),
                new IdentifierExpression("source"),
                BinaryOp.Assign);
            inner.Statements = new List<IAstNode>
            {
                assignment,
                new ConditionExpression(new IdentifierExpression("cached"))
                    { JumpTo = returning.Start, ElseTo = secondBody.Start }
            };
            returning.Statements = new List<IAstNode>
                { new ReturnExpression(new IdentifierExpression("zero")) };
            firstBody.Statements = new List<IAstNode>
                { new InvokeExpression("firstBody") };
            secondBody.Statements = new List<IAstNode>
                { new InvokeExpression("secondBody") };

            var context = new DecompileContext();
            context.Blocks.AddRange(
                new[] { root, inner, returning, firstBody, secondBody });
            context.UpdateBlockTable();
            var graph = new ControlFlowGraphAnalysis(context);

            Assert.IsFalse(DecisionDagAnalyzer.TryAnalyzeReturnGuard(
                context, graph, root, out _));
            Assert.AreEqual(2, inner.Statements.Count);
            Assert.AreSame(assignment, inner.Statements[0]);
            Assert.IsFalse(inner.Hidden,
                "拒绝三终点候选时不能留下赋值内联或隐藏状态");
        }

        [TestMethod]
        public void DecisionDagReturnAnalyzerRejectsExternallySharedReturn()
        {
            var root = new Block(0) { Id = 0, End = 0 };
            var returning = new Block(1) { Id = 1, End = 1 };
            var body = new Block(2) { Id = 2, End = 2 };
            var external = new Block(3) { Id = 3, End = 3 };
            root.To.AddRange(new[] { returning, body });
            returning.From.AddRange(new[] { root, external });
            body.From.Add(root);
            root.Statements = new List<IAstNode>
            {
                new ConditionExpression(new IdentifierExpression("reject"))
                    { JumpTo = returning.Start, ElseTo = body.Start }
            };
            var returnExpression = new ReturnExpression(
                new IdentifierExpression("sharedResult"));
            returning.Statements = new List<IAstNode> { returnExpression };
            body.Statements = new List<IAstNode>
                { new InvokeExpression("normalBody") };
            external.Statements = new List<IAstNode>();

            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { root, returning, body, external });
            context.UpdateBlockTable();
            var graph = new ControlFlowGraphAnalysis(context);

            Assert.IsFalse(DecisionDagAnalyzer.TryAnalyzeReturnGuard(
                context, graph, root, out _));
            Assert.IsFalse(returning.Hidden,
                "共享 return 必须留给父级顺序区域，不能复制进局部计划");
        }

        [TestMethod]
        public void TerminalReturnReconciliationRestoresSharedFinalReturn()
        {
            var entry = new Block(0) { Id = 0, End = 0 };
            var left = new Block(1) { Id = 1, End = 1 };
            var right = new Block(2) { Id = 2, End = 2 };
            var terminal = new Block(3) { Id = 3, End = 3 };
            entry.To.AddRange(new[] { left, right });
            left.From.Add(entry);
            right.From.Add(entry);
            left.To.Add(terminal);
            right.To.Add(terminal);
            terminal.From.AddRange(new[] { left, right });
            terminal.Statements = new List<IAstNode>
            {
                new ReturnExpression(new ConstantExpression(new TjsInt(0)))
            };

            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { entry, left, right, terminal });
            var structured = new BlockStatement();
            structured.Statements.Add(new ExpressionStatement(
                new InvokeExpression("work")));

            new TerminalReturnReconciliationPass().Process(context, structured);

            Assert.AreEqual(2, structured.Statements.Count);
            Assert.IsInstanceOfType<ExpressionStatement>(structured.Statements[^1]);
            Assert.IsInstanceOfType<ReturnExpression>(
                ((ExpressionStatement)structured.Statements[^1]).Expression,
                "补回的返回必须保持语句包装，写出时才能生成分号和换行");
        }

        [TestMethod]
        public void TerminalReturnReconciliationRejectsOriginalFallthrough()
        {
            var entry = new Block(0) { Id = 0, End = 0 };
            var decision = new Block(1) { Id = 1, End = 1 };
            var fallthrough = new Block(2) { Id = 2, End = 2 };
            var left = new Block(3) { Id = 3, End = 3 };
            var right = new Block(4) { Id = 4, End = 4 };
            var terminal = new Block(5) { Id = 5, End = 5 };
            entry.To.AddRange(new[] { decision, fallthrough });
            decision.From.Add(entry);
            fallthrough.From.Add(entry);
            decision.To.AddRange(new[] { left, right });
            left.From.Add(decision);
            right.From.Add(decision);
            left.To.Add(terminal);
            right.To.Add(terminal);
            terminal.From.AddRange(new[] { left, right });
            terminal.Statements = new List<IAstNode>
            {
                new ReturnExpression(new ConstantExpression(new TjsInt(0)))
            };

            var context = new DecompileContext();
            context.Blocks.AddRange(
                new[] { entry, decision, fallthrough, left, right, terminal });
            var structured = new BlockStatement();
            structured.Statements.Add(new ExpressionStatement(
                new InvokeExpression("work")));

            new TerminalReturnReconciliationPass().Process(context, structured);

            Assert.AreEqual(1, structured.Statements.Count,
                "原 CFG 存在自然落空路径时不能猜测默认返回值");
        }

        [TestMethod]
        public void TerminalReturnReconciliationDoesNotDuplicateAstReturn()
        {
            var entry = new Block(0) { Id = 0, End = 0 };
            var left = new Block(1) { Id = 1, End = 1 };
            var right = new Block(2) { Id = 2, End = 2 };
            var terminal = new Block(3) { Id = 3, End = 3 };
            entry.To.AddRange(new[] { left, right });
            left.From.Add(entry);
            right.From.Add(entry);
            left.To.Add(terminal);
            right.To.Add(terminal);
            terminal.From.AddRange(new[] { left, right });
            terminal.Statements = new List<IAstNode>
            {
                new ReturnExpression(new ConstantExpression(new TjsInt(0)))
            };

            var context = new DecompileContext();
            context.Blocks.AddRange(new[] { entry, left, right, terminal });
            var structured = new BlockStatement();
            structured.Statements.Add(new ExpressionStatement(
                new ReturnExpression(new ConstantExpression(new TjsInt(1)))));

            new TerminalReturnReconciliationPass().Process(context, structured);

            Assert.AreEqual(1, structured.Statements.Count,
                "结构化 AST 已经终止时不能重复追加返回");
        }

        [TestMethod]
        public void InvokeKeepsExplicitThisAndGlobalInstances()
        {
            var explicitThis = new IdentifierExpression("this", IdentifierType.This);
            var implicitThis = new IdentifierExpression(string.Empty, IdentifierType.ThisProxy);
            var global = new IdentifierExpression("global", IdentifierType.Global);

            Assert.IsFalse(new InvokeExpression("run") { Instance = explicitThis }.HideInstance);
            Assert.IsTrue(new InvokeExpression("run") { Instance = implicitThis }.HideInstance);
            Assert.IsFalse(new InvokeExpression("run") { Instance = global }.HideInstance);
        }

        [TestMethod]
        public void PathPredicateFactorsCommonCallsWithoutDuplicatingEvaluation()
        {
            var isEmpty = new IdentifierExpression("isEmpty");
            var following = new InvokeExpression("following");
            var limit = new IdentifierExpression("limit");
            var leading = new IdentifierExpression("leading");
            var weakPair = new InvokeExpression("weakPair");

            var whenEmpty = following.Or(limit);
            var whenPresent = leading.And(following).Or(weakPair).Or(limit);
            var merged = PathPredicate.Combine(
                isEmpty,
                PathPredicate.From(whenEmpty),
                PathPredicate.From(whenPresent));

            Assert.IsNotNull(merged?.Expression);
            Assert.AreEqual(1, CountExpressionReference(merged.Expression, following),
                "互斥路径共享的调用只能在合并条件中出现一次");
            Assert.AreEqual(1, CountExpressionReference(merged.Expression, limit),
                "两个分支末尾的公共条件应被提取一次");
            Assert.AreEqual(1, CountExpressionReference(merged.Expression, weakPair));
        }

        [TestMethod]
        public void UnaryNotKeepsBinaryOperandParentheses()
        {
            var instanceOf = new BinaryExpression(
                new IdentifierExpression("value"),
                new ConstantExpression(new TjsString("Function")),
                BinaryOp.InstanceOf);
            var body = new BlockStatement();
            var negated = new UnaryExpression(instanceOf, UnaryOp.Not);
            // 结构化阶段替换子树后 Parent 可能仍指向旧节点；写出正确性不能
            // 依赖这个缓存关系仍然完整。
            instanceOf.Parent = null;
            body.Statements.Add(new ExpressionStatement(negated));
            var text = new StringWriter();

            new TjsWriter(text).WriteBlock(body);

            StringAssert.Contains(text.ToString(), "!(value instanceof \"Function\");");
        }

        [TestMethod]
        public void BooleanNormalizationKeepsShortCircuitOrderWhileRemovingOuterNot()
        {
            var missing = new BinaryExpression(
                new IdentifierExpression("value"),
                new ConstantExpression(TjsVoid.Void),
                BinaryOp.Congruent);
            var disabled = new UnaryExpression(
                new IdentifierExpression("enabled"), UnaryOp.Not);
            var expression = new UnaryExpression(
                missing.Or(disabled), UnaryOp.Not);
            var body = new BlockStatement();
            body.Statements.Add(new ExpressionStatement(expression));

            new StructuredAstNormalizationPass().Process(null, body);
            var text = new StringWriter();
            new TjsWriter(text).WriteBlock(body);

            StringAssert.Contains(text.ToString(),
                "value !== void && enabled;");
        }

        [TestMethod]
        public void TestSemanticControlFlowRecovery()
        {
            var path = "..\\..\\..\\Res\\SemanticControlFlow.tjs.comp";
            var result = new Decompiler(path).Decompile();

            StringAssert.Contains(result, "var owner;");
            StringAssert.Contains(result, "var number;");
            Assert.IsTrue(Regex.IsMatch(result,
                    @"(?m)^\{\r?\n\s+var v\d+ = System\.getArgument\(""-furikiri-scope-probe""\);"),
                "顶层局部槽必须恢复到显式语句块内，不能编译成全局成员");
            Assert.AreEqual(1, CountOccurrences(result, "class Child"),
                "嵌套类只能由所属类递归输出一次");
            StringAssert.Contains(result, "class ValidationNestedOwner");
            Assert.IsTrue(Regex.IsMatch(result,
                    @"class\s+ValidationNestedOwner[^\{]*\{\s+class\s+Child\b",
                    RegexOptions.Singleline),
                "嵌套类不能被扁平化到顶层");
            Assert.IsFalse(Regex.IsMatch(result, @"(?m)^class\s+Child\b"),
                "嵌套类不能额外输出为顶层类");
            StringAssert.Contains(result, "new ValidationNestedOwner.Child()");
            StringAssert.Contains(result, "class DuplicatePropertyOwner");
            Assert.IsFalse(Regex.IsMatch(result,
                    @"class\s+(?:SemanticControlFlow|DuplicatePropertyOwner)[^{]*\{(?:(?!\bfunction\b|\bproperty\b).)*\breturn;",
                    RegexOptions.Singleline),
                "类初始化字节码的隐式末尾 return 不应出现在成员声明之前");
            Assert.AreEqual(2, CountOccurrences(result, "property sharedValue"),
                "不同类中的同名属性不能被全局名称索引覆盖");
            var cachedSwitchBody = SliceBetween(
                result, "function cachedSwitchInvocation", "function callOnce");
            Assert.AreEqual(1, CountOccurrences(cachedSwitchBody, "toLowerCase()"),
                "switch 控制调用必须只求值一次，不能复制到每个 else-if");
            var cachedSwitchLocal = Regex.Match(cachedSwitchBody,
                @"var (v\d+) = p3\[0\]\.toLowerCase\(\);");
            Assert.IsTrue(cachedSwitchLocal.Success,
                "switch 控制调用应缓存到合成局部变量");
            StringAssert.Contains(cachedSwitchBody,
                $"switch ({cachedSwitchLocal.Groups[1].Value})");
            StringAssert.Contains(cachedSwitchBody, "case \"second\":");
            var directSwitchBody = SliceBetween(
                result, "function directSwitchInvocation", "function distinctMemberFallback");
            Assert.AreEqual(1, CountOccurrences(directSwitchBody, "toLowerCase()"),
                "未显式缓存临时槽的 switch 控制调用也必须只求值一次");
            var directSwitchLocal = Regex.Match(directSwitchBody,
                @"var (v\d+) = p3\.toLowerCase\(\);");
            Assert.IsTrue(directSwitchLocal.Success,
                "直接 switch 控制调用应根据多 case CFG 恢复求值缓存");
            StringAssert.Contains(directSwitchBody,
                $"switch ({directSwitchLocal.Groups[1].Value})");
            StringAssert.Contains(directSwitchBody, "case \"second\":");
            StringAssert.Contains(result,
                @"return (p3 - p4) \ 2 + (p3 + p4) % 3;");
            var repeatedReceiver = Regex.Match(result,
                @"function repeatedMemberReceiver\([^)]*\)\s*\{(?<body>.*?)\r?\n    \}",
                RegexOptions.Singleline);
            Assert.IsTrue(repeatedReceiver.Success,
                "找不到重复成员接收者回归函数");
            Assert.AreEqual(1,
                CountOccurrences(repeatedReceiver.Groups["body"].Value, "getDimensions()"),
                "with 控制对象调用必须只求值一次");
            var chainedSideEffect = Regex.Match(result,
                @"function chainedSideEffectAssignment\([^)]*\)\s*\{(?<body>.*?)\r?\n    \}",
                RegexOptions.Singleline);
            Assert.IsTrue(chainedSideEffect.Success, "找不到链式副作用赋值回归函数");
            Assert.AreEqual(1,
                CountOccurrences(chainedSideEffect.Groups["body"].Value, "produce()"),
                "链式赋值右值调用不能重复求值");
            StringAssert.Contains(chainedSideEffect.Groups["body"].Value,
                "chainOuter = chainInner = produce();");
            var chainedLocal = Regex.Match(result,
                @"function chainedLocalAssignment\([^)]*\)\s*\{(?<body>.*?)\r?\n    \}",
                RegexOptions.Singleline);
            Assert.IsTrue(chainedLocal.Success, "找不到局部链式赋值回归函数");
            Assert.AreEqual(1,
                CountOccurrences(chainedLocal.Groups["body"].Value, "produce()"),
                "成员与局部变量共享的右值调用不能重复求值");
            StringAssert.Matches(chainedLocal.Groups["body"].Value,
                new Regex(@"var v\d+ = (?:this\.)?cached = produce\(\);"));
            var sharedTerminalReturn = SliceBetween(
                result, "function sharedTerminalReturn", "function sideEffectGuard");
            Assert.AreEqual(1,
                CountOccurrences(sharedTerminalReturn, "finishShared("),
                "公共返回调用应只输出一次，不能复制到提前返回分支");
            StringAssert.Contains(sharedTerminalReturn, "if (v4 >= 0)");
            StringAssert.Contains(result,
                "if (p4 === void || typeof p3.isBitmap == \"Object\" && !p3.isBitmap())");
            StringAssert.Contains(result, "if (p3 !== void && p3.pos !== void)");
            StringAssert.Contains(result, "switch (p3.mode)");
            StringAssert.Contains(result, "case \"\":");
            StringAssert.Contains(result, "case \"pile\":");
            StringAssert.Contains(result, "case \"alpha\":");
            StringAssert.Contains(result, "case \"addalpha\":");
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
            Assert.IsTrue(Regex.IsMatch(result,
                    @"return\s+\(?p3\s*\|\|\s*\(?p4\s*&&\s*p5\)?\)?;"),
                "布尔 Phi 化简不得改变 AND/OR 的结合关系");
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
            var groupedSwitchBody = SliceBetween(
                result, "function groupedSwitch", "function guard");
            StringAssert.Contains(groupedSwitchBody, "switch (p3)");
            StringAssert.Contains(groupedSwitchBody, "case 1:");
            StringAssert.Contains(groupedSwitchBody, "case 2:");
            StringAssert.Contains(result, "second();");
            StringAssert.Contains(result, "third();");
            StringAssert.Contains(result, "afterSwitch();");
            StringAssert.Contains(result, "if (v8 && v9)");
            StringAssert.Contains(result, "v7 += p6[v9 >> 2];");
            StringAssert.Contains(result,
                "if (v12 != 1 || v11 == 3 && v9 > 4)");
            StringAssert.Contains(result, "v7 += p4[v12] + p5[v11];");
            StringAssert.Contains(result, "v7 += p5[v11];");
            var switchLoopBody = SliceBetween(
                result, "function switchLoop", "function tryThenElseChoice");
            Assert.AreEqual(1, CountOccurrences(switchLoopBody, "var v8 = 0;"),
                "循环内重置已有标志变量时不应重复输出 var 声明");
            StringAssert.Contains(result, "if (v9 >= \"0\" && v9 <= \"9\")");
            StringAssert.Contains(result, "v7 += p4[+v9];");
            StringAssert.Contains(result, "v7 += v9;");
            var delayedForBody = SliceBetween(result, "function delayedForInitializer", "function rangeBreak");
            StringAssert.Contains(delayedForBody, "for (var v5 = 0; v5 < count_0; v5++)");
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
                "if (v6 && (name_0 = v6.name) != \"\")", StringComparison.Ordinal);
            var nestedAssignedGuard = assignedGuardBody.Contains("if (v6)", StringComparison.Ordinal) &&
                                      assignedGuardBody.Contains("name_0 = v6.name;", StringComparison.Ordinal) &&
                                      assignedGuardBody.Contains("if (name_0 != \"\")", StringComparison.Ordinal);
            Assert.IsTrue(combinedAssignedGuard || nestedAssignedGuard,
                "带赋值的短路条件应恢复为组合式或等价的嵌套式");
            Assert.IsTrue(assignedGuardBody.IndexOf("name_0 = v6.name", StringComparison.Ordinal) <
                          assignedGuardBody.IndexOf("consume(v6);", StringComparison.Ordinal),
                "带赋值的短路条件必须继续保护后续副作用");
            StringAssert.Contains(result,
                "if (p3 == \"\" || p4[p3] === void || (states_0 = p4[p3].states) === void)");
            StringAssert.Contains(result, "consume(states_0);");
            var stringEvaluationBody = SliceBetween(
                result, "function conditionalStringEvaluation", "function constructorMember");
            StringAssert.Contains(stringEvaluationBody, "if (v4 >= 0)");
            StringAssert.Contains(stringEvaluationBody, "&& !Scripts.eval(");
            Assert.IsFalse(Regex.IsMatch(stringEvaluationBody,
                    @"(?m)^\s*[A-Za-z_$][\w$]*\s*(?:==|!=|===|!==|>=|<=|>|<)\s*[^;]+;\s*$"),
                "字符串判定链不能留下脱离 if 的裸比较");
            Assert.IsTrue(
                stringEvaluationBody.IndexOf("setVisible(p3, 1);", StringComparison.Ordinal) >
                stringEvaluationBody.IndexOf("Scripts.eval", StringComparison.Ordinal),
                "正常路径的共享载荷必须保留在字符串早退保护之后");
            var prefixedPayloadBody = SliceBetween(
                result, "function prefixedNestedPayload", "function rangeBreak");
            StringAssert.Contains(prefixedPayloadBody, "if (currentCh !== void)");
            Assert.IsTrue(
                prefixedPayloadBody.IndexOf("currentLevel = p5;", StringComparison.Ordinal) <
                prefixedPayloadBody.IndexOf("if (currentCh !== void)", StringComparison.Ordinal),
                "内层 currentCh 保护必须位于 changed 分支的前置赋值之后");
            Assert.IsTrue(
                prefixedPayloadBody.IndexOf("copyClipboard(p4);", StringComparison.Ordinal) >
                prefixedPayloadBody.IndexOf("syncAll();", StringComparison.Ordinal),
                "公共尾部不能被吸入内层 currentCh 分支");
            var nestedReturnGuardBody = SliceBetween(
                result, "function nestedPayloadReturnGuard", "function nestedReverseLoops");
            StringAssert.Contains(nestedReturnGuardBody, "if (p3 == \"until\")");
            StringAssert.Contains(nestedReturnGuardBody, "if (v5 < 0)");
            StringAssert.Contains(nestedReturnGuardBody, "if (v5 < 6)");
            StringAssert.Contains(nestedReturnGuardBody, "else\r\n        {");
            Assert.IsTrue(
                nestedReturnGuardBody.IndexOf("return finishWait(v5);", StringComparison.Ordinal) >
                nestedReturnGuardBody.LastIndexOf("else", StringComparison.Ordinal),
                "公共返回必须保留在 mode 分支之后，不能被吸入 until 分支");
            var sharedLoopTailBody = SliceBetween(
                result, "function sharedLoopTailAfterNestedBranches", "function sideEffectGuard");
            Assert.IsFalse(sharedLoopTailBody.Contains("continue;", StringComparison.Ordinal),
                "循环公共尾部不能被误恢复成某一分支独占的 continue");
            var sharedTailAssignment = sharedLoopTailBody.LastIndexOf(" = 0;", StringComparison.Ordinal);
            Assert.IsTrue(
                sharedTailAssignment > sharedLoopTailBody.IndexOf("closeIndent();", StringComparison.Ordinal) &&
                sharedTailAssignment < sharedLoopTailBody.IndexOf("return", StringComparison.Ordinal),
                "每轮状态清理必须位于两路嵌套更新之后");
            var invocationReturnBody = SliceBetween(
                result, "function sequentialInvocationReturnGuards", "function sequentialNestedCallGuards");
            Assert.AreEqual(3, CountOccurrences(invocationReturnBody, "commitLine()"),
                "并列调用守卫不能被路径谓词复制求值");
            Assert.AreEqual(3, CountOccurrences(invocationReturnBody, "if (commitLine())"),
                "每个调用早退都应保留为所属分支中的叶子 if");
            Assert.IsTrue(
                invocationReturnBody.LastIndexOf(" = 0;", StringComparison.Ordinal) >
                invocationReturnBody.LastIndexOf("if (commitLine())", StringComparison.Ordinal),
                "未命中调用守卫时的状态回退必须保留在最终 else 中");
            var conditionalLatchBody = SliceBetween(
                result, "function conditionalLatchLoop", "function delayedForInitializer");
            StringAssert.Contains(conditionalLatchBody, "while (v4 < count_0)");
            StringAssert.Contains(conditionalLatchBody, "count_0--;");
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
            StringAssert.Contains(emptySwitchBody, "switch (v5)");
            StringAssert.Contains(emptySwitchBody, "case \"\\\\\":");
            StringAssert.Contains(emptySwitchBody, "case \"k\":");
            Assert.AreEqual(1, CountOccurrences(emptySwitchBody, "after(v5);"));
            Assert.AreEqual(1, CountOccurrences(emptySwitchBody, "accept(v5);"));
            Assert.IsFalse(emptySwitchBody.Contains("v5 == \"\\\\\";", StringComparison.Ordinal),
                "空 case 的比较不能脱离 switch 成为裸表达式");
            var escapedDispatchBody = SliceBetween(
                result, "function escapedSwitchSharedDispatch", "function guardedDelete");
            StringAssert.Contains(escapedDispatchBody, "if (v5 == \"\\\\\")");
            StringAssert.Contains(escapedDispatchBody, "switch (v5)");
            StringAssert.Contains(escapedDispatchBody, "case \"\\\\\":");
            StringAssert.Contains(escapedDispatchBody, "case \"k\":");
            StringAssert.Contains(escapedDispatchBody, "default:");
            StringAssert.Contains(escapedDispatchBody, "continue;");
            StringAssert.Contains(escapedDispatchBody, "normal(v5);");
            StringAssert.Contains(escapedDispatchBody, "if (v4 < p3.length)");
            StringAssert.Contains(escapedDispatchBody, "percent(v5);");
            Assert.IsFalse(Regex.IsMatch(escapedDispatchBody, @"(?m)^\s*v4 >= p3\.length;\s*$"),
                "循环尾部作为 false 分支时，范围保护不能退化为裸反向比较");
            Assert.IsFalse(escapedDispatchBody.Contains("else if (v5 == \"\\n\")", StringComparison.Ordinal),
                "空 case 的 break 必须落入 switch 后的公共字符处理，不能仍挂在外层 else");
            Assert.AreEqual(1, CountOccurrences(escapedDispatchBody, "normal(v5);"),
                "共享字符处理不能因 switch 分支恢复而丢失或重复");
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
            var loopTryBody = SliceBetween(
                result, "function loopTryCleanup", "function memberDefaults");
            StringAssert.Contains(loopTryBody, "beforeTry(v4);");
            StringAssert.Contains(loopTryBody, "try");
            StringAssert.Contains(loopTryBody, "consume(p3[v4]);");
            StringAssert.Contains(loopTryBody, "catch");
            StringAssert.Contains(loopTryBody, "afterTry(v4);");
            Assert.IsFalse(loopTryBody.Contains("continue;", StringComparison.Ordinal),
                "try 的结构性 EXTRY 出口不能被误写为 continue");
            Assert.IsFalse(loopTryBody.Contains("__e", StringComparison.Ordinal),
                "未使用的异常寄存器不能泄漏到循环正文");
            var initializedSideEffectBody = SliceBetween(
                result, "function initializedSideEffectGuard", "function loopShortCircuitBody");
            StringAssert.Contains(initializedSideEffectBody,
                "if (off_0 && setVisible(0) || on_0 && setVisible(1))");
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
            Assert.IsTrue(result.Contains("p3 &= ~(p4 | p5);", StringComparison.Ordinal) ||
                          result.Contains("p3 = p3 & ~(p4 | p5);", StringComparison.Ordinal),
                "位运算原地更新必须保留赋值副作用");
            StringAssert.Contains(result, "if (p3.top === void)");
            StringAssert.Contains(result, "if (p3.left === void)");
            StringAssert.Contains(result, "if (p3.right === void)");
            StringAssert.Contains(result,
                "handlers = %[\r\n            \"pimage\" => SemanticControlFlow.loadPartialImage,");
            StringAssert.Contains(result,
                "\r\n            \"ptext\" => SemanticControlFlow.drawReconstructibleText\r\n        ];");
            StringAssert.Contains(result,
                "if (p3 === void || p3 == \"\" || p3.substr(0, 3) == \"eye\" && +p3.substr(3) == p4 || p3.substr(0, 3) == \"lip\" && +p3.substr(3) == p5)");
            StringAssert.Contains(result, "var object_0 = p3[p4].object;");
            StringAssert.Contains(result,
                "if (object_0 === void || !isvalid object_0 || object_0.visible && object_0.enabled)");
            StringAssert.Contains(result, "consume((new PSBFile(p3)).root);");
            var colorKeyBody = SliceBetween(
                result, "function colorKeyDispatch", "function compoundDoWhile");
            Assert.AreEqual(4, CountOccurrences(colorKeyBody, "if ("),
                "多路赋值谓词的入口根块不能被嵌套逻辑隐藏");
            StringAssert.Contains(colorKeyBody, "p3 = COLOR_NONE;");
            StringAssert.Contains(colorKeyBody, "p3 = COLOR_ADAPT;");
            StringAssert.Contains(colorKeyBody, "p3 = +p3;");
            StringAssert.Contains(colorKeyBody, "p3 = +p3 + 50331648;");
            StringAssert.Contains(colorKeyBody, "return p3;");
            var nestedGuardBody = SliceBetween(
                result, "function nestedGuardFallthrough", "function nestedLoopExit");
            StringAssert.Contains(nestedGuardBody, "p3");
            StringAssert.Contains(nestedGuardBody, "if (v4 > 0)");
            StringAssert.Contains(nestedGuardBody, "return 1;");
            StringAssert.Contains(nestedGuardBody, "v4++;");
            StringAssert.Contains(nestedGuardBody, "consume(v4);");
            StringAssert.Contains(nestedGuardBody, "return 0;");
            Assert.IsTrue(nestedGuardBody.IndexOf("consume(v4);", StringComparison.Ordinal) >
                          nestedGuardBody.LastIndexOf("v4++;", StringComparison.Ordinal),
                "提前返回分支后的公共继续语句必须保留在外层 if 之后");
            Assert.IsFalse(nestedGuardBody.Contains(
                    "else\r\n        {\r\n            consume(v4);", StringComparison.Ordinal),
                "公共继续语句不应成为多路条件链的 default else");
            var bitFlagsBody = SliceBetween(
                result, "function conditionalBitFlags", "function conditionalLatchLoop");
            StringAssert.Contains(bitFlagsBody, "p3 |= FONT_ITALIC;");
            StringAssert.Contains(bitFlagsBody, "p3 |= FONT_UNDERLINE;");
            StringAssert.Contains(bitFlagsBody, "p3 |= FONT_STRIKEOUT;");
            var menuTrackingBody = SliceBetween(
                result, "function menuTracking", "function mode");
            StringAssert.Contains(menuTrackingBody,
                "\r\n        menuShowing = (!p3 ? 0 : (p4 ? -1 : 1));");
            StringAssert.Contains(menuTrackingBody, "setVisible(1);");
            StringAssert.Contains(menuTrackingBody, "setVisible(0);");
            StringAssert.Contains(menuTrackingBody, "updateMenu();");
            var phiBranchBody = SliceBetween(
                result, "function conditionalPhiBranch", "function constructorMember");
            StringAssert.Contains(phiBranchBody, "var v5 = (p4 ? 4 : 3);");
            StringAssert.Contains(phiBranchBody, "if (p3)");
            StringAssert.Contains(phiBranchBody, "first(v5 + 0);");
            StringAssert.Contains(phiBranchBody, "second(v5 + 1);");
            StringAssert.Contains(phiBranchBody, "third(v5 + 2);");
            StringAssert.Contains(phiBranchBody, "afterChoice();");
            var nestedCleanupBody = SliceBetween(
                result, "function nestedCleanup", "function nestedEqualityDispatch");
            StringAssert.Contains(nestedCleanupBody, "if (p3.enabled)");
            StringAssert.Contains(nestedCleanupBody, "if (p3.count >= 0)");
            StringAssert.Contains(nestedCleanupBody, "p3.count++;");
            StringAssert.Contains(nestedCleanupBody, "p3.enabled = void;");
            Assert.IsTrue(nestedCleanupBody.IndexOf("p3.enabled = void;", StringComparison.Ordinal) >
                          nestedCleanupBody.IndexOf("p3.count++;", StringComparison.Ordinal),
                "内层可选更新后的清理赋值必须仍受外层条件保护");
            Assert.IsFalse(Regex.IsMatch(nestedCleanupBody,
                    @"(?m)^\s+(?:p3\.enabled|p3\.count\s*(?:===|!==|==|!=|<=|>=|<|>).*)\s*;\s*$"),
                "成员守卫不能退化成裸属性或裸比较语句");
            StringAssert.Contains(result,
                "consume([p3.first, p3.second, p3.third]);");
            Assert.IsFalse(result.Contains("([])[0]", StringComparison.Ordinal),
                "匿名数组初始化不能拆成多个互不相同的空数组");
            var memberCleanupBody = SliceBetween(
                result, "function memberLookupCleanup", "function menuTracking");
            StringAssert.Contains(memberCleanupBody,
                "if (p4 !== void && (p3.dresses == void || p3.dresses[p4] == void))");
            StringAssert.Contains(memberCleanupBody, "p4 = void;");
            Assert.IsFalse(Regex.IsMatch(memberCleanupBody,
                    @"(?m)^\s+p3\.dresses(?:\[p4\])?\s*(?:===|!==|==|!=).+;\s*$"),
                "已折叠的成员查找守卫不能退化成裸比较语句");
            var dictionaryReturnBody = SliceBetween(
                result, "function conditionalDictionaryReturn", "function conditionalLatchLoop");
            StringAssert.Contains(dictionaryReturnBody, "if (p3)");
            Assert.IsFalse(dictionaryReturnBody.Contains("else", StringComparison.Ordinal),
                "两臂都返回时字节码无法区分显式 else 与顺序早退，统一输出顺序守卫");
            StringAssert.Contains(dictionaryReturnBody, "p4 ? -p3.x : p3.x");
            Assert.AreEqual(2, CountOccurrences(dictionaryReturnBody, "\"x\" =>"),
                "合流状态中的同一组字典 SPIS 写入只能聚合一次");
            var memberFallbackBody = SliceBetween(
                result, "function distinctMemberFallback", "function emptySwitchCase");
            StringAssert.Contains(memberFallbackBody,
                "((p3.value !== void) ? +p3.value : +p4.value)");
            var nestedAssignmentBody = SliceBetween(
                result, "function nestedConditionalAssignment", "function nestedDefault");
            StringAssert.Contains(nestedAssignmentBody, "if (p3.levels === void)");
            Assert.AreEqual(2, CountOccurrences(nestedAssignmentBody, "if (info_0 === void)"),
                "三元式合流后的完整 if/else 必须在两个外围分支中分别恢复");
            Assert.AreEqual(4, CountOccurrences(nestedAssignmentBody, "zoomValue ="),
                "条件两臂的成员赋值不能被提升为无条件连续赋值");
            Assert.IsFalse(Regex.IsMatch(nestedAssignmentBody,
                    @"(?m)^\s+info_0\s*(?:===|!==|==|!=).+;\s*$"),
                "合流后的局部变量判断不能退化成裸比较语句");
            var exclusiveBranchesBody = SliceBetween(
                result, "function mutuallyExclusiveNestedBranches", "function nestedDispatchSharedTail");
            StringAssert.Contains(exclusiveBranchesBody, "if (!p3)");
            StringAssert.Contains(exclusiveBranchesBody, "if (p4)");
            StringAssert.Contains(exclusiveBranchesBody, "if (p5)");
            Assert.IsTrue(exclusiveBranchesBody.IndexOf("if (p5)", StringComparison.Ordinal) >
                          exclusiveBranchesBody.IndexOf("else", StringComparison.Ordinal),
                "第二个内层判断必须位于外层互斥分支的 else 区域中");
            StringAssert.Contains(exclusiveBranchesBody, "else if (p5)");
            StringAssert.Contains(exclusiveBranchesBody, "finishResize();");
            var sharedTailBody = SliceBetween(
                result, "function nestedDispatchSharedTail", "function nestedEqualityDispatch");
            StringAssert.Contains(sharedTailBody, "if (p3)");
            StringAssert.Contains(sharedTailBody, "if (p4)");
            StringAssert.Contains(sharedTailBody, "p5 == KEY_RIGHT || p5 == KEY_NEXT");
            StringAssert.Contains(sharedTailBody,
                "if (p5 == KEY_ESCAPE || p5 == KEY_RETURN || p5 == KEY_SPACE)");
            Assert.AreEqual(4, CountOccurrences(sharedTailBody, "nextPage();"),
                "竖排与横排分派中的四条下一页路径都必须保留");
            Assert.AreEqual(1, CountOccurrences(sharedTailBody, "closeView();"));
            Assert.IsTrue(sharedTailBody.LastIndexOf(
                              "if (p5 == KEY_ESCAPE", StringComparison.Ordinal) >
                          sharedTailBody.LastIndexOf(
                              "p5 == KEY_RIGHT || p5 == KEY_NEXT", StringComparison.Ordinal),
                "关闭键判断必须留在滚动分派之后，不能并入其 else-if 链");
            var nestedDictionaryBody = SliceBetween(
                result, "function nestedDictionaryAfterTernary", "function nestedDispatchSharedTail");
            StringAssert.Contains(nestedDictionaryBody, "opacity = (p3 ? 0 : 255);");
            StringAssert.Contains(nestedDictionaryBody, "beginAction(%[");
            StringAssert.Contains(nestedDictionaryBody, "\"opacity\" => %[");
            Assert.AreEqual(1, CountOccurrences(nestedDictionaryBody, "MoveAction"),
                "嵌套字典构造器只能聚合一次，不能在假 Phi 的两侧重复展开");
            Assert.IsFalse(Regex.IsMatch(nestedDictionaryBody,
                    "\\)\\[\"(?:value|time|opacity)\"\\]\\s*="),
                "集合字面量字段必须折叠进构造器，不能写入三元临时字典");
            var stringAddBody = SliceBetween(
                result, "function stringConcatNestedAdd", "function switchLoop");
            StringAssert.Contains(stringAddBody, "\"line: \" + (p3 + 1) + \"\\n\"");
            Assert.IsFalse(stringAddBody.Contains(
                    "\"line: \" + p3 + 1", StringComparison.Ordinal),
                "字符串拼接右侧的同优先级数值加法必须保留括号");
            var catchThrowBody = SliceBetween(
                result, "function catchVariableInThrow", "function cleanup");
            var catchParameter = Regex.Match(catchThrowBody, @"catch\((v\d+)\)");
            Assert.IsTrue(catchParameter.Success,
                "异常变量仅在 throw 表达式中使用时也必须输出 catch 参数");
            StringAssert.Contains(catchThrowBody, catchParameter.Groups[1].Value + ".message");
            var unusedCatchBody = SliceBetween(
                result, "function unusedCatchThenLocal", "function waitWithHandlers");
            StringAssert.Contains(unusedCatchBody, "catch\r\n");
            StringAssert.Contains(unusedCatchBody, "var value_0 = p3.value;");
            StringAssert.Contains(unusedCatchBody, "consume(value_0);");
            StringAssert.Contains(result,
                "if (p3 != \"\" && p4 == p3 || p3 == \"\" && probe(p5) != \"\")");
            var returnGuardBody = SliceBetween(
                result, "function returnGuardContinuation", "function sequentialInvocationReturnGuards");
            StringAssert.Contains(returnGuardBody, "if (!p3 && p4 !== void)");
            StringAssert.Contains(returnGuardBody, "if (p5 < 0)");
            StringAssert.Contains(returnGuardBody, "p5 += p6;");
            Assert.IsTrue(Regex.IsMatch(returnGuardBody,
                    @"return\s+consume\(p5,\s*\(?p3\s*\|\|\s*p7\s*!==\s*void\s*&&\s*p5\s*>\s*p7\)?\);"),
                "返回参数中的短路表达式必须保持 OR/AND 的结合顺序");
            Assert.IsFalse(returnGuardBody.Contains("else\r\n", StringComparison.Ordinal),
                "提前返回守卫后的公共语句不应被包进 else");
            var sharedContinuationBody = SliceBetween(
                result, "function guardedSharedContinuation", "function initializedSideEffectGuard");
            StringAssert.Contains(sharedContinuationBody, "if (p4)");
            StringAssert.Contains(sharedContinuationBody, "emptyCheck(p5);");
            StringAssert.Contains(sharedContinuationBody, "var v6 = -1;");
            StringAssert.Contains(sharedContinuationBody, "consume(v6);");
            Assert.IsFalse(sharedContinuationBody.Contains(
                    "else\r\n        {\r\n            var v6 = -1;", StringComparison.Ordinal),
                "仅终止分支不能迫使正常公共继续块成为多路条件链的 default else");
            var sharedExplicitReturnBody = SliceBetween(
                result, "function sharedExplicitReturn", "function sideEffectGuard");
            StringAssert.Contains(sharedExplicitReturnBody, "if (isAuto)");
            StringAssert.Contains(sharedExplicitReturnBody, "if (targetLine !== void)");
            StringAssert.Contains(sharedExplicitReturnBody, "if (p3 !== p4)");
            StringAssert.Contains(sharedExplicitReturnBody, "reportMismatch();");
            StringAssert.Contains(sharedExplicitReturnBody, "return 1;");
            StringAssert.Contains(sharedExplicitReturnBody, "return fallbackLabel();");
            Assert.IsTrue(
                sharedExplicitReturnBody.IndexOf("return 1;", StringComparison.Ordinal) <
                sharedExplicitReturnBody.IndexOf("return fallbackLabel();", StringComparison.Ordinal),
                "内层显式返回不能吞掉外围路径共享的返回块");
            var waitHandlersStart = result.IndexOf(
                "function waitWithHandlers", StringComparison.Ordinal);
            Assert.IsTrue(waitHandlersStart >= 0, "找不到闭包字典等待回归函数");
            var waitHandlersBody = result[waitHandlersStart..];
            StringAssert.Contains(waitHandlersBody,
                "if ((p4 === void || +p4) && clickSkipEnabled)");
            StringAssert.Contains(waitHandlersBody, "\"click\" => function");
            StringAssert.Contains(waitHandlersBody, "p3.stop();");
            StringAssert.Contains(waitHandlersBody, "\"click_arg\" => p3");
            Assert.AreEqual(2, CountOccurrences(waitHandlersBody, "consume(%["),
                "可跳过和普通等待两路都必须保留自己的字典调用");
            Assert.IsFalse(waitHandlersBody.Contains("consume(%[]);", StringComparison.Ordinal),
                "菱形 CFG 重入不能用新的空构造器覆盖已聚合的闭包字典");
            var equalityDefaultBody = SliceBetween(
                result, "function equalityDefaultTrailingIf", "function gatedMultiWay");
            StringAssert.Contains(equalityDefaultBody, "else if (p3 == \"base\")");
            StringAssert.Contains(equalityDefaultBody, "if (v6)");
            StringAssert.Contains(equalityDefaultBody, "p4[0] = \"\";");
            Assert.IsFalse(equalityDefaultBody.Contains("v6;", StringComparison.Ordinal),
                "相等比较链 default 区域中的条件不能退化为裸局部变量");
            var dispatchTailBody = SliceBetween(
                result, "function equalityDispatchSharedTail",
                "function escapedSwitchSharedDispatch");
            var midiBranch = dispatchTailBody.IndexOf(
                "else if (p3 == \"midi\")", StringComparison.Ordinal);
            var sharedEnabled = dispatchTailBody.IndexOf("if (p4)", StringComparison.Ordinal);
            Assert.IsTrue(midiBranch >= 0 && sharedEnabled > midiBranch,
                "多路相等分派后的公共 enabled 尾部必须位于完整类型链之后");
            Assert.IsFalse(dispatchTailBody.Contains("switch (", StringComparison.Ordinal),
                "没有选择快照的连续 if/else-if 不能仅按标签数量改写成 switch");
            Assert.AreEqual(1, CountOccurrences(dispatchTailBody, "playNow();"));
            Assert.IsTrue(dispatchTailBody.IndexOf("currentKind = p3;", StringComparison.Ordinal) >
                          dispatchTailBody.IndexOf("playNow();", StringComparison.Ordinal),
                "公共最终赋值不能被吸入相等分派的 default 分支");
            StringAssert.Contains(result, "function nestedSharedUpdateGuard");
            StringAssert.Contains(result,
                "if (p3 != currentName || isClear() || isShown())");
            StringAssert.Contains(result, "currentName = p3;");
            StringAssert.Contains(result, "finishUpdate();");
            StringAssert.Contains(result, "function mergedObjectGate");
            StringAssert.Contains(result, "if (p3 === global ||");
            StringAssert.Contains(result, "getAddr(p3) == getBase(p3)");
            StringAssert.Contains(result, "accept(p3);");
            Assert.IsFalse(result.Contains("/*phi:", StringComparison.Ordinal),
                "可结构化的嵌套三元表达式不应退化为 Phi 占位符");
            Assert.IsFalse(Regex.IsMatch(result, @"(?m)^\s+(?:p\d+|v\d+);\s*$"),
                "已消费的条件标志不能残留为裸参数或局部变量语句");
            Assert.IsFalse(Regex.IsMatch(result,
                    @"(?m)^\s*[A-Za-z_$][\w$]*(?:(?:\.[A-Za-z_$][\w$]*)|\[[^\]\r\n]+\])*\s*(?:===|!==|==|!=|(?<!>)>=|(?<!<)<=|>(?![>=])|<(?![<=-]))\s*[^;]+;\s*$"),
                "局部变量或成员比较不能脱离所属 guard 成为裸表达式");
            Assert.AreEqual(1, CountOccurrences(result, "p3(p4)"),
                "有返回值的动态调用不应同时作为独立语句重复输出");
            var conditionalAssignmentBody = SliceBetween(
                result, "function conditionalAssignmentReturn", "function conditionalBitFlags");
            Assert.AreEqual(2,
                CountOccurrences(conditionalAssignmentBody, "(v4 = p3.indexOf("),
                "两个三元分支都必须保留各自的位置赋值");
            Assert.IsFalse(conditionalAssignmentBody.Contains("if (", StringComparison.Ordinal),
                "已进入三元条件的赋值不应再保留为空 if 外壳");
            var casePayloadBody = SliceBetween(
                result, "function validationCaseNestedPayload", "function validationMultiStageLoop");
            StringAssert.Contains(casePayloadBody, "if (p4 != \"\")");
            StringAssert.Contains(casePayloadBody, "if (p4.indexOf(\":\"))");
            StringAssert.Contains(casePayloadBody, "else\r\n");
            var nestedSwitchInCase = SliceBetween(
                result, "function validationNestedSwitchInCase", "function validationPayloadDispatch");
            Assert.AreEqual(2, CountOccurrences(nestedSwitchInCase, "switch ("),
                "外层 case 中已经证明的内层分派也必须恢复为 switch");
            StringAssert.Contains(nestedSwitchInCase, "case \"char\":");
            StringAssert.Contains(nestedSwitchInCase, "case \"disp\":");
            StringAssert.Contains(nestedSwitchInCase, "default:");
            Assert.AreEqual(1, CountOccurrences(nestedSwitchInCase, "acceptNamed("),
                "内层 default 不能再顺序执行另一 case 的载荷");
            Assert.AreEqual(1, CountOccurrences(nestedSwitchInCase, "acceptParts("));
            var loopSwitchFallthrough = SliceBetween(
                result,
                "function validationLoopSwitchDefaultFallthrough",
                "function validationMultiStageLoop");
            StringAssert.Contains(loopSwitchFallthrough, "switch (");
            StringAssert.Contains(loopSwitchFallthrough, "case \"copy\":");
            StringAssert.Contains(loopSwitchFallthrough, "default:");
            StringAssert.Matches(loopSwitchFallthrough,
                new Regex(@"if \(v\d+ === void\)\s*\{\s*break;"));
            Assert.IsLessThan(
                loopSwitchFallthrough.IndexOf("default:", StringComparison.Ordinal),
                loopSwitchFallthrough.IndexOf("case \"copy\":", StringComparison.Ordinal),
                "复制成功路径必须自然贯穿 default，失败路径只退出 switch");
            Assert.AreEqual(1, CountOccurrences(loopSwitchFallthrough, "copyItem("));
            var multiStageLoopBody = SliceBetween(
                result, "function validationMultiStageLoop", "property sharedValue");
            StringAssert.Matches(multiStageLoopBody,
                new Regex(@"if \(v\d+\[0\] != """" && v\d+\[1\] != """"\)"));
            StringAssert.Contains(multiStageLoopBody, "consume(");
            var multipleBreaksBody = SliceBetween(
                result, "function validationMultipleBreaks", "function validationPayloadDispatch");
            StringAssert.Contains(multipleBreaksBody, "while (");
            StringAssert.Matches(multipleBreaksBody, new Regex(@"v\d+ >= p4"));
            StringAssert.Contains(multipleBreaksBody, "break;");

            var nestedLoopExitBody = SliceBetween(
                result, "function nestedLoopExit", "function nestedReverseLoops");
            StringAssert.Contains(nestedLoopExitBody, "if (v6 == \",\")");
            StringAssert.Contains(nestedLoopExitBody, "break;");
            Assert.IsTrue(
                nestedLoopExitBody.IndexOf("break;", StringComparison.Ordinal) <
                nestedLoopExitBody.IndexOf("v5 += v6;", StringComparison.Ordinal),
                "子循环退出边不能丢失，否则遇到分隔符时会继续执行正文或形成死循环");

            var loopExitPayloadBody = SliceBetween(
                result, "function validationLoopExitPayload", "function validationMultipleBreaks");
            Assert.AreEqual(1, CountOccurrences(loopExitPayloadBody, "consume("),
                "命中分支正文不能被复制或提升到循环外");
            StringAssert.Contains(loopExitPayloadBody, "continue;");
            StringAssert.Contains(loopExitPayloadBody, "break;");
            Assert.IsTrue(
                loopExitPayloadBody.IndexOf("consume(", StringComparison.Ordinal) <
                loopExitPayloadBody.IndexOf("break;", StringComparison.Ordinal) &&
                loopExitPayloadBody.IndexOf("break;", StringComparison.Ordinal) <
                loopExitPayloadBody.IndexOf("finishSearch();", StringComparison.Ordinal),
                "命中正文和 break 必须留在循环内，公共尾部仍在循环之后");

            var loopBreakChainBody = SliceBetween(
                result, "function validationLoopBreakChain", "function validationLoopExitPayload");
            StringAssert.Contains(loopBreakChainBody, "break;");
            StringAssert.Contains(loopBreakChainBody, "= v7;");
            Assert.IsTrue(
                loopBreakChainBody.IndexOf("break;", StringComparison.Ordinal) <
                loopBreakChainBody.IndexOf("consume(", StringComparison.Ordinal),
                "循环出口后的初始化和调用不能被吸入 break 分支");

            var prefixLoopBody = SliceBetween(
                result, "function validationPrefixConditionLoop", "function validationSharedConditionDiamond");
            StringAssert.Contains(prefixLoopBody, "while (++p4 < count_0)");
            Assert.IsFalse(prefixLoopBody.Contains("p4++ < count_0", StringComparison.Ordinal),
                "条件前完成的增量内联后必须保持前置语义");
            StringAssert.Contains(prefixLoopBody, "break;");

            var trySuccessBreakBody = SliceBetween(
                result, "function validationTrySuccessBreak", "property sharedValue");
            StringAssert.Contains(trySuccessBreakBody, "try");
            Assert.IsTrue(
                trySuccessBreakBody.IndexOf("saveNow();", StringComparison.Ordinal) <
                trySuccessBreakBody.IndexOf("break;", StringComparison.Ordinal),
                "try 的正常 EXTRY 出口若离开循环，必须保留显式 break");
            StringAssert.Contains(trySuccessBreakBody, "finishSave();");

            var guardedLeafBody = SliceBetween(
                result, "function validationGuardedLeafIncrement", "property sharedValue");
            StringAssert.Contains(guardedLeafBody, "if (p3 != void && p3.inc)");
            StringAssert.Contains(guardedLeafBody, "if (p3.value >= 0)");
            Assert.IsTrue(
                guardedLeafBody.IndexOf("p3.value++;", StringComparison.Ordinal) <
                guardedLeafBody.IndexOf("p3.inc = void;", StringComparison.Ordinal),
                "受控递增必须留在内层 if 中，清理赋值仍位于外层守卫内");

            var nestedReturnBody = SliceBetween(
                result, "function validationNestedReturnDispatch", "function validationPayloadDispatch");
            StringAssert.Contains(nestedReturnBody, "p3 === void || p3 == \"base\"");
            StringAssert.Contains(nestedReturnBody, "if (p3[0] != \"m\")");
            StringAssert.Contains(nestedReturnBody, "if (p3 == \"message\")");
            StringAssert.Contains(nestedReturnBody, "messages[+p3.substr(7)]");
            StringAssert.Contains(nestedReturnBody, "layers[+p3]");

            var independentCaseBody = SliceBetween(
                result,
                "function validationSharedCaseBodyWithIndependentPhi",
                "function validationSharedConditionDiamond");
            StringAssert.Contains(independentCaseBody, "case 1:");
            StringAssert.Contains(independentCaseBody, "case 2:");
            Assert.IsTrue(Regex.IsMatch(independentCaseBody,
                    @"(v\d+) \|= \(\(p3 == 1\) \? 4 : 8\);[\s\S]*\1 \|= 16;"),
                "共享 case 正文中的独立三元求值不能被误当成外层标签并删除");
            Assert.AreEqual(3, CountOccurrences(independentCaseBody, "|="),
                "两个共享 case 写入和另一 case 写入都必须保留");

            var sharedDiamondBody = SliceBetween(
                result, "function validationSharedConditionDiamond", "function validationTerminalGuard");
            StringAssert.Contains(sharedDiamondBody, "p3.a !== void");
            StringAssert.Contains(sharedDiamondBody, "p3.b !== void || p3.c !== void");
            StringAssert.Contains(sharedDiamondBody, "p3.d !== void");
            StringAssert.Contains(sharedDiamondBody, "values[0] = p3.b;");
            StringAssert.Contains(sharedDiamondBody, "values[0] = p3.c;");
            StringAssert.Contains(sharedDiamondBody, "values[1] = p3.d;");
            StringAssert.Contains(multipleBreaksBody, "drawAt(");
            Assert.IsFalse(Regex.IsMatch(multipleBreaksBody,
                    @"\b(?:if|else)[^{\r\n]*\{\r?\n\s*\}"),
                "多重循环退出边不能留下空 if/else");
            var keyDispatchBody = SliceBetween(
                result, "function validationKeyDispatch", "function validationMultipleBreaks");
            StringAssert.Contains(keyDispatchBody,
                "p4 == KEY_UP && !(p5 & SHIFT_MASK)");
            StringAssert.Contains(keyDispatchBody,
                "p4 == KEY_TAB && p5 & SHIFT_MASK");
            StringAssert.Contains(keyDispatchBody,
                "p4 == KEY_TAB && !(p5 & SHIFT_MASK)");
            StringAssert.Contains(keyDispatchBody, "else if (!p3");
            Assert.AreEqual(1, CountOccurrences(keyDispatchBody, "selectPrev();"));
            Assert.AreEqual(1, CountOccurrences(keyDispatchBody, "selectNext();"));
            var payloadDispatchBody = SliceBetween(
                result, "function validationPayloadDispatch", "function validationTerminalGuard");
            StringAssert.Contains(payloadDispatchBody, "clickLock = 0;");
            StringAssert.Contains(payloadDispatchBody, "if (p3 || p4 == -1)");
            StringAssert.Contains(payloadDispatchBody, "if (hasClick())");
            StringAssert.Contains(payloadDispatchBody, "else if (p4 != -1 && p5 == TYPE_EDIT)");
            StringAssert.Contains(payloadDispatchBody, "processLink(p4);");
            var terminalGuardBody = SliceBetween(
                result, "function validationTerminalGuard", "function waitWithHandlers");
            StringAssert.Contains(terminalGuardBody,
                "if (!p3 || !p4 && !hasClick())");
            StringAssert.Contains(terminalGuardBody, "return fallbackKey();");
            StringAssert.Contains(terminalGuardBody, "dispatchKey();");
            var combinedTargetBody = SliceBetween(
                result, "function validationCombinedTarget", "function validationKeyDispatch");
            StringAssert.Contains(combinedTargetBody,
                "if (p3 > p4 || p3 >= p4 && p5 >= p6)");
            StringAssert.Contains(combinedTargetBody, "stopAtTarget();");
            Assert.IsFalse(Regex.IsMatch(combinedTargetBody,
                    @"\b(?:if|else)[^{\r\n]*\{\r?\n\s*\}"),
                "已经合并进谓词的冗余决策壳不能输出为空分支");
            var postStateBody = SliceBetween(
                result, "function validationPostStateRecheck", "function validationPrefixConditionLoop");
            StringAssert.Contains(postStateBody,
                "if (pendingState === void && typeof p3 == \"Object\")");
            StringAssert.Contains(postStateBody, "if (pendingState !== void)");
            Assert.IsFalse(postStateBody.Contains("else if (pendingState === void)",
                    StringComparison.Ordinal),
                "修改状态后的再次判断是顺序控制流，不能接成前一个判断的 else-if");
            StringAssert.Contains(postStateBody, "consume(pendingState = p4);");
            Assert.IsTrue(postStateBody.IndexOf("pendingState = p4", StringComparison.Ordinal) >
                          postStateBody.IndexOf("if (pendingState !== void)", StringComparison.Ordinal),
                "状态赋值必须继续受第二个 if 保护");
            var postfixMemberBody = SliceBetween(
                result,
                "function validationPostfixMemberArgument",
                "function validationPostStateRecheck");
            StringAssert.Contains(postfixMemberBody, "consume(number++);");
            StringAssert.Contains(postfixMemberBody, "consume(p3[p4]++);");
            Assert.IsFalse(postfixMemberBody.Contains("number++;\r\n        consume(number)",
                    StringComparison.Ordinal),
                "成员后置自增的旧值必须直接传入调用，不能先递增再重新读取");
            var sharedDecisionDagBody = SliceBetween(
                result,
                "function validationSharedDecisionDagPayload",
                "function validationTerminalGuard");
            Assert.AreEqual(2, CountOccurrences(sharedDecisionDagBody, "drawPose("),
                "共享决策 DAG 中的两次短路调用都必须保留且各执行至多一次");
            StringAssert.Contains(sharedDecisionDagBody,
                "!drawPose(p3, p4) && (p4 == p5 || !drawPose(p3, p5))");
            StringAssert.Contains(sharedDecisionDagBody, "reportError();");
            StringAssert.Contains(sharedDecisionDagBody, "afterDraw();");
            Assert.IsTrue(
                sharedDecisionDagBody.IndexOf("reportError();", StringComparison.Ordinal) <
                sharedDecisionDagBody.IndexOf("afterDraw();", StringComparison.Ordinal),
                "公共尾部必须位于决策 DAG 之后，不能被吸入载荷分支");
            Assert.IsFalse(sharedDecisionDagBody.Contains("?", StringComparison.Ordinal),
                "控制流路径不应泄漏为重复调用的条件表达式");
            var sequentialConstantGuards = SliceBetween(
                result,
                "function validationSequentialConstantReturnGuards",
                "function validationSharedConditionDiamond");
            StringAssert.Contains(sequentialConstantGuards, "if (p3 === void)");
            StringAssert.Contains(sequentialConstantGuards, "if (v4 === void)");
            Assert.AreEqual(3, CountOccurrences(sequentialConstantGuards, "return 0;"),
                "两个顺序早退和末尾返回都必须保留");
            Assert.IsFalse(sequentialConstantGuards.Contains("else", StringComparison.Ordinal),
                "已终止 then 的 else 正文应恢复为后续顺序语句");
            var sharedFinalReturn = SliceBetween(
                result,
                "function validationSharedFinalReturn",
                "function validationTerminalGuard");
            Assert.AreEqual(2, CountOccurrences(sharedFinalReturn, "return 0;"),
                "嵌套守卫中的早退和函数尾公共返回都必须保留");
            Assert.AreEqual(1, CountOccurrences(sharedFinalReturn, "return 1;"));
            Assert.IsTrue(
                sharedFinalReturn.LastIndexOf("return 0;", StringComparison.Ordinal) >
                sharedFinalReturn.IndexOf("return 1;", StringComparison.Ordinal),
                "公共默认返回必须位于完整守卫区域之后");
            var localPrefixNestedRegion = SliceBetween(
                result,
                "function validationLocalPrefixNestedRegion",
                "function validationLoopBreakChain");
            StringAssert.Contains(localPrefixNestedRegion, "if (p3 !== void)");
            StringAssert.Contains(localPrefixNestedRegion, "if (p4 !== void)");
            StringAssert.Contains(localPrefixNestedRegion, "p3.get(");
            StringAssert.Contains(localPrefixNestedRegion, "fallbackInfo(p4)");
            StringAssert.Contains(localPrefixNestedRegion, "consume(");
            Assert.IsFalse(localPrefixNestedRegion.Contains(
                    "p3 !== void && p4 !== void", StringComparison.Ordinal),
                "局部初始化不能触发载荷链抢占并合并两层区域");
            var twoPayloadFallback = SliceBetween(
                result,
                "function validationTwoPayloadFallback",
                "function waitWithHandlers");
            StringAssert.Contains(twoPayloadFallback,
                "if (p3 !== void && p4 !== void)");
            StringAssert.Contains(twoPayloadFallback, "v5 = p3 / p4;");
            StringAssert.Contains(twoPayloadFallback, "else");
            StringAssert.Contains(twoPayloadFallback,
                "v5 = fallbackScale();");
            Assert.AreEqual(1,
                CountOccurrences(twoPayloadFallback, "fallbackScale()"),
                "两条条件失败边必须共享同一个回退载荷，不能丢失或复制求值");
            var catchVoidReturn = SliceBetween(
                result,
                "function validationCatchVoidReturn",
                "function validationLocalSwap");
            StringAssert.Contains(catchVoidReturn, "try");
            StringAssert.Contains(catchVoidReturn, "catch");
            StringAssert.Contains(catchVoidReturn, "invalidate p3;");
            StringAssert.Contains(catchVoidReturn, "return;");
            StringAssert.Contains(catchVoidReturn, "consume(p3);");
            Assert.IsTrue(
                catchVoidReturn.IndexOf("invalidate p3;", StringComparison.Ordinal) <
                catchVoidReturn.IndexOf("return;", StringComparison.Ordinal) &&
                catchVoidReturn.IndexOf("return;", StringComparison.Ordinal) <
                catchVoidReturn.IndexOf("consume(p3);", StringComparison.Ordinal),
                "catch 的提前 return 必须保留，不能让异常路径落入正常尾部");
            var evalRecovery = SliceBetween(
                result,
                "function validationEvalDiscard",
                "function validationGuardedLeafIncrement");
            StringAssert.Contains(evalRecovery, "(p3)!;");
            Assert.IsTrue(Regex.IsMatch(evalRecovery,
                    @"var\s+v\d+\s*=\s*\(p3\)!\s+incontextof\s+p4;"),
                "EVAL 的结果必须写回寄存器，供后续 incontextof 使用");
            Assert.AreEqual(2, CountOccurrences(evalRecovery, "(p3)!"),
                "有结果 EVAL 与丢弃结果 EEXP 都必须各保留一次动态求值");
            var characterOperators = SliceBetween(
                result,
                "function validationCharacterOperators",
                "function validationCombinedTarget");
            Assert.IsTrue(Regex.IsMatch(characterOperators,
                    @"var\s+v\d+\s*=\s*#p3;"),
                "ASC 必须恢复为取得字符编码的 # 运算符");
            Assert.IsTrue(Regex.IsMatch(characterOperators,
                    @"var\s+v\d+\s*=\s*\$p4;"),
                "CHR 必须恢复为由字符编码生成字符的 $ 运算符");
            Assert.IsTrue(Regex.IsMatch(characterOperators,
                    @"var\s+v\d+\s*=\s*\$\(p4 \+ 1\);"),
                "字符编码运算的复合操作数必须用括号保持原求值范围");
            var localSwap = SliceBetween(
                result,
                "function validationLocalSwap",
                "property sharedValue");
            StringAssert.Contains(localSwap, "p3 <-> p4;");
            Assert.IsFalse(Regex.IsMatch(localSwap,
                    @"p3\s*=\s*p4;\s*p4\s*=\s*p3;"),
                "交换不能被输出为会覆盖左侧旧值的顺序赋值");
            var propertySwap = SliceBetween(
                result,
                "function validationPropertySwap",
                "property sharedValue");
            StringAssert.Contains(propertySwap, "p3 <-> p4.value;");
            StringAssert.Contains(propertySwap, "p3 <-> p4.child.value;");
            StringAssert.Contains(propertySwap, "p3 <-> p4[p5];");
            StringAssert.Contains(propertySwap, "p4.reverse <-> p3;");
            StringAssert.Contains(propertySwap, "p4[p5] <-> p3;");
            Assert.IsFalse(Regex.IsMatch(propertySwap,
                    @"p3\s*=\s*p4(?:\.value|\[p5\]);"),
                "属性交换不能退化为先覆盖左值的普通赋值序列");
            var propertyPairSwap = SliceBetween(
                result,
                "function validationPropertyPairSwap",
                "function validationPropertySwap");
            StringAssert.Contains(propertyPairSwap,
                "p3.width <-> p3.height;");
            StringAssert.Contains(propertyPairSwap,
                "p3[p5] <-> p4[p6];");
            Assert.IsFalse(Regex.IsMatch(propertyPairSwap,
                    @"p3\.width\s*=\s*p3\.height;\s*p3\.height\s*=\s*p3\.width;"),
                "属性之间的交换必须保留两侧旧值，不能输出覆盖式顺序赋值");
            var swapBeforeTry = SliceBetween(
                result,
                "function validationSwapBeforeTry",
                "function validationTerminalGuard");
            Assert.AreEqual(3, CountOccurrences(swapBeforeTry, "cached <-> p3;"),
                "catch 入口覆盖异常槽，不能阻止 try 前后的原子交换恢复");
            StringAssert.Contains(swapBeforeTry, "try");
            StringAssert.Contains(swapBeforeTry, "catch(");
            StringAssert.Contains(swapBeforeTry, "throw");
            var forwardedProperty = SliceBetween(
                result,
                "property forwardedValue",
                "property sharedValue");
            Assert.AreEqual(2,
                CountOccurrences(forwardedProperty,
                    "*(&global.SemanticControlFlow.sharedValue incontextof this)"),
                "改绑上下文后的属性对象读取和写入都必须保留一元 * 语义");
            StringAssert.Contains(forwardedProperty,
                "*(&global.SemanticControlFlow.sharedValue incontextof this) = p3;");
            var guardedShortCircuitAssignment = SliceBetween(
                result,
                "function validationGuardedShortCircuitAssignment",
                "function validationKeyDispatch");
            Assert.AreEqual(1,
                CountOccurrences(guardedShortCircuitAssignment,
                    "typeof System.getDisplayMonitors"),
                "短路赋值的存在性守卫不能同时保留为独立 if 并复制到结果表达式");
            Assert.AreEqual(1,
                CountOccurrences(guardedShortCircuitAssignment,
                    "System.getDisplayMonitors()"),
                "短路链中的受保护调用必须只求值一次");
            Assert.IsTrue(Regex.IsMatch(guardedShortCircuitAssignment,
                    @"typeof System\.getDisplayMonitors != \""undefined\""\s*&&\s*\(v\d+ = System\.getDisplayMonitors\(\)\) !== void"),
                "受守卫的局部赋值应回到布尔短路链中的原求值位置");
            var typeSwitch = SliceBetween(
                result,
                "function validationTypeSwitch",
                "function waitWithHandlers");
            StringAssert.Contains(typeSwitch, "switch (typeof p3)");
            Assert.AreEqual(1, CountOccurrences(typeSwitch, "typeof p3"),
                "typeof switch 的选择表达式必须只求值一次");
            StringAssert.Contains(typeSwitch, "case \"Integer\":");
            StringAssert.Contains(typeSwitch, "case \"String\":");
            StringAssert.Contains(typeSwitch, "default:");
            var propertySwitch = SliceBetween(
                result,
                "function validationPropertySwitch",
                "function validationSequentialConstantReturnGuards");
            StringAssert.Contains(propertySwitch, "switch (p3.kind)");
            StringAssert.Contains(propertySwitch, "case 1:");
            StringAssert.Contains(propertySwitch, "case 2:");
            Assert.AreEqual(1, CountOccurrences(propertySwitch, "p3.kind"),
                "属性 switch 的选择值必须只输出一次");
            var computedSwitch = SliceBetween(
                result,
                "function validationComputedSwitch",
                "function validationCrossBlockReceiver");
            StringAssert.Contains(computedSwitch, "switch (p3 % 3)");
            Assert.AreEqual(1, CountOccurrences(computedSwitch, "p3 % 3"),
                "计算式 switch 的选择值必须只输出一次");
            var independentPropertyReads = SliceBetween(
                result,
                "function validationIndependentPropertyReads",
                "function validationKeyDispatch");
            Assert.AreEqual(0, CountOccurrences(independentPropertyReads, "switch ("),
                "独立属性读取不能误合并为 switch");
            Assert.AreEqual(2, CountOccurrences(independentPropertyReads, "p3.kind"),
                "独立 if/else-if 应保留两次属性读取");
            var expressionCaseSwitch = SliceBetween(
                result,
                "function validationExpressionCaseSwitch",
                "function validationFactoredBooleanPhi");
            StringAssert.Contains(expressionCaseSwitch, "switch (p3)");
            StringAssert.Contains(expressionCaseSwitch, "case p4.first:");
            StringAssert.Contains(expressionCaseSwitch, "case p4.alternate:");
            StringAssert.Contains(expressionCaseSwitch, "case p4.second:");
            Assert.IsLessThan(
                expressionCaseSwitch.IndexOf("case p4.second:", StringComparison.Ordinal),
                expressionCaseSwitch.IndexOf("case p4.first:", StringComparison.Ordinal),
                "表达式 case 标签必须保持原来的顺序求值");
            var twoLabelLocalSwitch = SliceBetween(
                result,
                "function validationTwoLabelLocalSwitch",
                "function validationTwoPayloadFallback");
            StringAssert.Contains(twoLabelLocalSwitch, "switch (");
            StringAssert.Contains(twoLabelLocalSwitch, "case -1:");
            StringAssert.Contains(twoLabelLocalSwitch, "case 1:");
            StringAssert.Contains(twoLabelLocalSwitch, "default:");
            var terminatingSwitchWithDefault = SliceBetween(
                result,
                "function validationTerminatingSwitchWithDefaultDeclaration",
                "function validationTerminatingSwitchWithoutDefault");
            StringAssert.Contains(
                terminatingSwitchWithDefault, "switch (typeof p3)");
            StringAssert.Contains(terminatingSwitchWithDefault, "default:");
            StringAssert.Matches(
                terminatingSwitchWithDefault,
                new Regex(@"default:\s+var v\d+ = ""other"";"),
                "显式 default 的局部声明必须留在 default 子句中");
            var terminatingSwitchWithoutDefault = SliceBetween(
                result,
                "function validationTerminatingSwitchWithoutDefault",
                "function validationTrySuccessBreak");
            StringAssert.Contains(
                terminatingSwitchWithoutDefault, "switch (typeof p3)");
            Assert.AreEqual(0,
                CountOccurrences(terminatingSwitchWithoutDefault, "default:"),
                "switch 后的顺序代码不能被伪造为 default");
            StringAssert.Matches(
                terminatingSwitchWithoutDefault,
                new Regex(@"}\s+var v\d+ = consumeOther\(p3\);"),
                "未命中区域应在 switch 结束后继续执行");
            var sharedEmptyCases = SliceBetween(
                result,
                "function validationSharedEmptyCases",
                "function validationSharedTerminalTypeCases");
            StringAssert.Contains(sharedEmptyCases, "switch (");
            StringAssert.Contains(sharedEmptyCases, "case -1:");
            StringAssert.Contains(sharedEmptyCases, "case 1:");
            StringAssert.Contains(sharedEmptyCases, "case 2:");
            StringAssert.Contains(sharedEmptyCases, "default:");
            Assert.AreEqual(1, CountOccurrences(sharedEmptyCases, "accept("));
            Assert.AreEqual(1, CountOccurrences(sharedEmptyCases, "reject("));
            var distinctEquivalentReturns = SliceBetween(
                result,
                "function validationDistinctEquivalentReturns",
                "function validationSharedTerminalTypeCases");
            Assert.AreEqual(2,
                CountOccurrences(distinctEquivalentReturns, "finishShared("),
                "来自两条 CALL 指令的同形返回表达式不能被当成公共求值合并");
            StringAssert.Contains(distinctEquivalentReturns, "consumeOne(");
            var singleCaseSwitch = SliceBetween(
                result,
                "function validationSingleCaseSwitch",
                "function validationSingleCaseSwitchContainingIf");
            StringAssert.Contains(singleCaseSwitch, "switch (typeof p3)");
            StringAssert.Contains(singleCaseSwitch, "case \"String\":");
            StringAssert.Contains(singleCaseSwitch, "default:");
            var singleCaseBreakSwitch = SliceBetween(
                result,
                "function validationSingleCaseBreakSwitch",
                "function validationSingleCaseSwitch");
            StringAssert.Contains(singleCaseBreakSwitch,
                "switch (typeof p3)");
            StringAssert.Contains(singleCaseBreakSwitch, "case \"String\":");
            var singleCaseSwitchContainingIf = SliceBetween(
                result,
                "function validationSingleCaseSwitchContainingIf",
                "function validationSpecialRealConstants");
            Assert.AreEqual(1, CountOccurrences(
                    singleCaseSwitchContainingIf, "switch ("),
                "case 内普通 if 不能重复认领外层 switch 的收尾跳转");
            StringAssert.Contains(singleCaseSwitchContainingIf,
                "if (p3 == \"target\")");
            var nestedSingleCaseDefaultSwitch = SliceBetween(
                result,
                "function validationNestedSingleCaseDefaultSwitch",
                "function validationNestedSwitchInCase");
            Assert.AreEqual(2, CountOccurrences(
                    nestedSingleCaseDefaultSwitch, "switch ("),
                "拥有独立 default 收尾的内层单 case switch 必须由自己认领");
            StringAssert.Contains(
                nestedSingleCaseDefaultSwitch, "switch (p3.level)");
            StringAssert.Contains(nestedSingleCaseDefaultSwitch, "case 0:");
            StringAssert.Contains(nestedSingleCaseDefaultSwitch, "default:");
            var defaultSharingCaseBody = SliceBetween(
                result,
                "function validationDefaultSharingCaseBody",
                "function validationDistinctEquivalentReturns");
            Assert.AreEqual(2,
                CountOccurrences(defaultSharingCaseBody, "switch ("));
            StringAssert.Contains(defaultSharingCaseBody, "case \"left\":");
            StringAssert.Contains(defaultSharingCaseBody, "default:");
            Assert.AreEqual(1,
                CountOccurrences(defaultSharingCaseBody, "consumeTwo(p3);"),
                "default 与 case 共用的正文不能被复制或移到 switch 之后");
            var memberAssignmentRead = SliceBetween(
                result,
                "function validationMemberAssignmentRead",
                "property forwardedValue");
            StringAssert.Contains(memberAssignmentRead,
                "memberAssignmentState = p3;");
            StringAssert.Contains(memberAssignmentRead,
                "if (memberAssignmentState === void)");
            Assert.AreEqual(0, CountOccurrences(memberAssignmentRead,
                    "if ((memberAssignmentState ="),
                "成员写入后必须重新读取，不能跳过可观察的 getter");
            StringAssert.Contains(result,
                "if (p3.x === void || p3.x > p4.x)");
            StringAssert.Contains(result,
                "if (p3.y === void || p3.y > p4.y)");
            StringAssert.Contains(result,
                "if (p3.x2 === void || p3.x2 < p4.x2)");
            StringAssert.Contains(result,
                "if (p3.y2 === void || p3.y2 < p4.y2)");
            Assert.IsFalse(result.Contains(
                "p3.x !== void && p3.x > p4.x",
                StringComparison.Ordinal),
                "共享载荷的短路 OR 不能被拆成反向 AND");
            var guardedSwitch = SliceBetween(
                result, "function validationSwitchGuardedSharedVoid",
                "function validationTerminalGuard");
            Assert.AreEqual(2, CountOccurrences(guardedSwitch, "if (p4 !== void)"),
                "两个 case 必须分别保留对象存在性 guard");
            StringAssert.Contains(guardedSwitch, "p4.face != \"\"");
            StringAssert.Contains(guardedSwitch, "if (p4.face.indexOf(\":\"))");
            Assert.IsTrue(CountOccurrences(guardedSwitch, "break;") >= 2,
                "guard 失败路径仍需退出各自 switch，不能贯穿后一 case");
            Assert.IsFalse(Regex.IsMatch(guardedSwitch,
                    @"(?m)^\s*p4\.face\s*(?:==|!=)\s*"""";"),
                "default 内对象判断不能退化为裸比较");
            var emptyBranchAroundNested = SliceBetween(
                result, "function validationEmptyBranchAroundNested",
                "property forwardedValue");
            StringAssert.Contains(emptyBranchAroundNested, "if (p3 != null)");
            StringAssert.Contains(emptyBranchAroundNested, "if (p4)");
            StringAssert.Contains(emptyBranchAroundNested, "p3.accept();");
            StringAssert.Contains(emptyBranchAroundNested, "p3.reject();");
            Assert.AreEqual(1,
                CountOccurrences(emptyBranchAroundNested, "p3.accept();"),
                "受保护副作用不能被复制到外层条件之外");
            var specialRealConstants = SliceBetween(
                result, "function validationSpecialRealConstants",
                "property forwardedValue");
            StringAssert.Contains(specialRealConstants, "return NaN;");
            StringAssert.Contains(specialRealConstants, "return Infinity;");
            StringAssert.Contains(specialRealConstants, "return -Infinity;");
            StringAssert.Contains(specialRealConstants, "return 1.25;");
            Assert.IsFalse(specialRealConstants.Contains("∞", StringComparison.Ordinal),
                "特殊实数必须使用 TJS2 规范关键字，不能依赖当前区域设置的显示符号");
            var multiCaseSwitchContainingIf = SliceBetween(
                result,
                "function validationMultiCaseSwitchContainingIf",
                "function validationNestedSingleCaseDefaultSwitch");
            Assert.AreEqual(1, CountOccurrences(
                    multiCaseSwitchContainingIf, "switch ("),
                "case 内条件不能复用外层多 case switch 的收尾跳转");
            StringAssert.Contains(multiCaseSwitchContainingIf,
                "if (p3.state == \"yes\")");
            StringAssert.Contains(multiCaseSwitchContainingIf,
                "else if (p3.state == \"no\")");
            var equivalentComputedIf = SliceBetween(
                result,
                "function validationEquivalentComputedIf",
                "function validationEvalDiscard");
            Assert.AreEqual(0, CountOccurrences(equivalentComputedIf, "switch ("),
                "没有 switch 收尾跳转证据的单个 if 不能误恢复为 switch");
            StringAssert.Contains(equivalentComputedIf, "if (typeof p3 == \"String\")");
            var computedIfContainingSwitch = SliceBetween(
                result,
                "function validationComputedIfContainingSwitch",
                "function validationComputedSwitch");
            Assert.AreEqual(1,
                CountOccurrences(computedIfContainingSwitch, "switch ("),
                "内层 switch 的收尾跳转不能把外层普通 if 误判成 switch");
            StringAssert.Contains(computedIfContainingSwitch,
                "if (typeof p3 == \"String\")");
            StringAssert.Contains(computedIfContainingSwitch, "switch (p3)");
            var sharedTerminalCases = SliceBetween(
                result,
                "function validationSharedTerminalTypeCases",
                "function validationSharedWithReceiver");
            StringAssert.Contains(sharedTerminalCases, "switch (typeof p3)");
            StringAssert.Contains(sharedTerminalCases, "case \"Integer\":");
            StringAssert.Contains(sharedTerminalCases, "case \"Real\":");
            Assert.AreEqual(1,
                CountOccurrences(sharedTerminalCases, "return makeNumber(p3);"),
                "Integer/Real 两个标签必须共享同一个数值返回正文");
            Assert.AreEqual(1, CountOccurrences(sharedTerminalCases, "typeof p3"),
                "共享终止 case 仍应保持 switch 选择值单次求值");
            var guardedTypeSwitch = SliceBetween(
                result,
                "function validationGuardedTypeSwitch",
                "function validationKeyDispatch");
            StringAssert.Contains(guardedTypeSwitch,
                "if (typeof v5 == \"Object\" && v5.stop)");
            StringAssert.Contains(guardedTypeSwitch, "switch (typeof v5)");
            Assert.AreEqual(2, CountOccurrences(guardedTypeSwitch, "typeof v5"),
                "循环退出守卫和后续 switch 应各自求值一次，路径谓词不能复制进 case");
            var blockScopedSlotReuse = SliceBetween(
                result,
                "function validationBlockScopedSlotReuse",
                "function validationCachedLoopBranch");
            Assert.IsTrue(Regex.IsMatch(blockScopedSlotReuse,
                    @"var (v\d+);[\s\S]*if \(p3\)[\s\S]*\1 = makeBranchValue\(\);[\s\S]*\}[\s\S]*\1 = makeFollowingValue\(\);"),
                "跨词法块复用的同一 VM 槽应提升声明，避免后续写入退化成成员写入");
            var forScopedSlotReuse = SliceBetween(
                result,
                "function validationForScopedSlotReuse",
                "function validationGuardedLeafIncrement");
            Assert.IsTrue(Regex.IsMatch(forScopedSlotReuse,
                    @"var (v\d+);[\s\S]*for \(\1 = 0;[\s\S]*\}[\s\S]*\1 = makeFollowingValue\(\);"),
                "for 初始化节和后续语句复用同一 VM 槽时应提升一次声明");
            var sharedWithReceiver = SliceBetween(
                result,
                "function validationSharedWithReceiver",
                "function validationTerminalGuard");
            Assert.AreEqual(1, CountOccurrences(sharedWithReceiver, "getPoint(p3)"),
                "with 控制对象的调用结果必须只求值一次");
            Assert.IsTrue(Regex.IsMatch(sharedWithReceiver,
                    @"var\s+(v\d+)\s*=\s*getPoint\(p3\);[\s\S]*\1\.x[\s\S]*\1\.y"),
                "共享的调用结果应缓存后再读取不同成员");
            var callableShortCircuit = Regex.Match(result,
                @"function validationCallableShortCircuit\(p3\)\s*\{(?<body>[^{}]*)\}");
            Assert.IsTrue(callableShortCircuit.Success,
                "应输出函数值短路调用测试方法");
            var callableLocal = Regex.Match(
                callableShortCircuit.Groups["body"].Value,
                @"var\s+([A-Za-z_]\w*)\s*=\s*p3\.callback;");
            Assert.IsTrue(callableLocal.Success,
                "应保留函数值局部变量");
            Assert.AreEqual(2,
                CountOccurrences(callableShortCircuit.Groups["body"].Value,
                    callableLocal.Groups[1].Value + "("),
                "短路条件中的函数值调用不能被额外输出为独立语句");
            var factoredBooleanPhi = SliceBetween(
                result,
                "function validationFactoredBooleanPhi",
                "function validationTerminalGuard");
            Assert.AreEqual(1, CountOccurrences(factoredBooleanPhi, "probe()"),
                "条件 Phi 两臂的公共调用尾项必须只输出一次");
            Assert.IsTrue(Regex.IsMatch(factoredBooleanPhi,
                    @"p3 && \(p4 \|\| p5\) && p6 && \(p7 \|\| probe\(\)\)"),
                "多路共享尾项应恢复为按源码顺序求值的短路条件");
            var guardedCallableReturn = SliceBetween(
                result,
                "function validationGuardedCallableReturn",
                "function validationGuardedLeafIncrement");
            StringAssert.Contains(guardedCallableReturn,
                "return p3 && (p4(KEY_RETURN) || p4(KEY_SPACE)) || p4(KEY_CONTROL);");
            Assert.AreEqual(3, CountOccurrences(guardedCallableReturn, "p4("),
                "返回 flag 的三路 Phi 必须保留外层守卫且不能复制调用");
            var guardedNestedTernary = SliceBetween(
                result,
                "function validationGuardedNestedTernary",
                "function validationGuardedShortCircuitAssignment");
            StringAssert.Contains(guardedNestedTernary,
                "p3 && p5.cameraZoom > 100 && p4 < p5.levels.count - 1");
            StringAssert.Contains(guardedNestedTernary, "? p4 + 1 : p4");
            StringAssert.Contains(result,
                "return p3[0].core.currentPageName;");
            var leadingEmptyCase = SliceBetween(
                result,
                "function validationLeadingEmptyCase",
                "function validationLocalPrefixNestedRegion");
            StringAssert.Contains(leadingEmptyCase, "switch (p4)");
            StringAssert.Contains(leadingEmptyCase, "case MODE_NONE:");
            StringAssert.Contains(leadingEmptyCase, "case MODE_HORIZONTAL:");
            StringAssert.Contains(leadingEmptyCase, "case MODE_VERTICAL:");
            StringAssert.Contains(leadingEmptyCase, "default:");
            StringAssert.Contains(leadingEmptyCase,
                "p3 |= STYLE_HORIZONTAL | STYLE_VERTICAL;");
            var assignedValueReceiver = SliceBetween(
                result,
                "function validationAssignedValueReceiver",
                "function validationBlockScopedSlotReuse");
            StringAssert.Matches(assignedValueReceiver, new Regex(
                @"var (?<saved>v\d+) = p4.values;\s*\(p3.values = \[\]\).assign\(\k<saved>\);"));
            StringAssert.Matches(assignedValueReceiver, new Regex(
                @"var (?<saved>v\d+) = p4\[p5\];\s*var (?<method>v\d+) = Dictionary.assign;\s*\(\k<method> incontextof \(p3\[p5\] = %\[\]\)\)\(\k<saved>\);"));
            StringAssert.Contains(assignedValueReceiver,
                "(p3.node = makeNode()).consume(p4);");
            Assert.AreEqual(1, CountOccurrences(assignedValueReceiver, "= []"),
                "数组构造结果写入属性后仍应只构造一次");
            Assert.AreEqual(1, CountOccurrences(assignedValueReceiver, "%[]"),
                "字典构造结果写入动态属性后仍应只构造一次");
            Assert.AreEqual(1, CountOccurrences(assignedValueReceiver, "makeNode()"),
                "普通调用结果写入属性后仍应只调用一次");
            var crossBlockReceiverStart = result.IndexOf(
                "function validationCrossBlockReceiver", StringComparison.Ordinal);
            Assert.IsTrue(crossBlockReceiverStart >= 0,
                "应输出跨块控制对象回归函数");
            var crossBlockReceiver = result.Substring(
                crossBlockReceiverStart,
                Math.Min(700, result.Length - crossBlockReceiverStart));
            Assert.AreEqual(1, CountOccurrences(crossBlockReceiver, "makeNode()"),
                "跨基本块存活的控制对象创建调用必须只求值一次");
            Assert.IsTrue(Regex.IsMatch(crossBlockReceiver,
                    @"var (v\d+) = makeNode\(\);[\s\S]*p3\.shared = \1;[\s\S]*\1\.first\(\);[\s\S]*\1\.second\(\);"),
                "跨块共享的调用结果应先物化到唯一局部变量，再由各分支共同使用");
            var alternativeProbe = SliceBetween(
                result,
                "function validationAlternativeProbe",
                "function validationCachedLoopBranch");
            Assert.AreEqual(2, CountOccurrences(alternativeProbe, "probe("),
                "两个 case 的析取守卫调用都必须保留且各只求值一次");
            StringAssert.Contains(alternativeProbe, "switch (typeof");
            StringAssert.Contains(alternativeProbe, "case \"String\":");
            StringAssert.Contains(alternativeProbe, "case \"Object\":");
            Assert.IsTrue(Regex.IsMatch(alternativeProbe,
                    @"if\s*\([^\r\n]*\|\|[^\r\n]*probe\("),
                "命中同一返回体的多条条件路径应恢复为一条完整析取谓词");
            var cachedLoopBranch = SliceBetween(
                result,
                "function validationCachedLoopBranch",
                "function validationCallableShortCircuit");
            Assert.AreEqual(1, CountOccurrences(cachedLoopBranch, "nextValue()"),
                "共享决策入口中的调用赋值必须只求值一次");
            Assert.IsTrue(Regex.IsMatch(cachedLoopBranch,
                    @"if \((?:a0|p3)\.count > 0\)[\s\S]*?else\s*\{\s*v\d+ = nextValue\(\);"),
                "nextValue 调用必须保持在队列为空的 else 分支内");
            Assert.AreEqual(1,
                CountOccurrences(cachedLoopBranch, "consumeFinal("),
                "两臂的公共尾调用不能被内层条件独占或复制");
            Assert.IsTrue(Regex.IsMatch(cachedLoopBranch,
                    @"if \(targetLine === void\)[\s\S]*?else\s*\{[\s\S]*?\.line > targetLine"),
                "目标行比较必须只在目标值有效的分支中执行");
            Assert.IsTrue(Regex.IsMatch(cachedLoopBranch,
                    @"\.add\([^\r\n]*\);\s*continue;"),
                "缓存当前值后必须结束本轮，不能继续执行公共尾调用");
            StringAssert.Contains(cachedLoopBranch, "if (p4)");
            StringAssert.Contains(cachedLoopBranch, "consumeFinal(");
            Assert.IsTrue(Regex.IsMatch(cachedLoopBranch,
                    @"v\d+\.line > targetLine \|\| v\d+\.line >= targetLine && v\d+\.count >= targetCount"),
                "多块 continue 守卫中的完整短路谓词必须保留");
            Assert.IsTrue(
                cachedLoopBranch.IndexOf("nextValue()", StringComparison.Ordinal) <
                cachedLoopBranch.IndexOf("consumeFinal", StringComparison.Ordinal),
                "分支中的单次赋值必须保持在公共处理之前");
            var loopIterationCompletion = SliceBetween(
                result,
                "function validationLoopIterationCompletion",
                "function validationLoopSwitchDefaultFallthrough");
            Assert.IsGreaterThanOrEqualTo(1,
                CountOccurrences(loopIterationCompletion, "continue;"),
                "需要跳过同层公共处理的 line 分派臂应显式结束本轮");
            Assert.IsTrue(Regex.IsMatch(loopIterationCompletion,
                    @"if \([^\r\n]*kind == ""line""\)[\s\S]*if \([^\r\n]*currentLine != [^\r\n]*\.line\)[\s\S]*trace\([^\r\n]*\);[\s\S]*\}\s*continue;"),
                "line 分派臂的 continue 必须位于内部更新条件之后");
            Assert.IsTrue(Regex.IsMatch(loopIterationCompletion,
                    @"else if \([^\r\n]*kind == ""count""\)[\s\S]*currentCount\+\+;[\s\S]*else\s*\{\s*consumeFinal"),
                "count 与普通值必须保持互斥，只有普通值落入公共处理");
            var compoundDoWhile = SliceBetween(
                result,
                "function validationCompoundDoWhileTarget",
                "function validationComputedIfContainingSwitch");
            Assert.IsTrue(Regex.IsMatch(compoundDoWhile,
                    @"if \([^\r\n]*\.line > targetLine \|\| [^\r\n]*\.line >= targetLine && [^\r\n]*\.count >= targetCount\)\s*\{\s*targetLine = void;"),
                "清除目标值的赋值必须保留在原复合条件内");
            Assert.IsTrue(Regex.IsMatch(compoundDoWhile,
                    @"while \([^\r\n]* < (?:a0|p3)\.count && targetLine != void\);"),
                "分散在多个尾块的 do-while 短路条件必须完整合并");
            var loopGuardedBreakBody = SliceBetween(
                result,
                "function validationLoopGuardedPayloadBreak",
                "function validationLoopSwitchDefaultFallthrough");
            Assert.IsTrue(Regex.IsMatch(loopGuardedBreakBody,
                    @"typeof (v\d+)\.speed !== ""undefined"" && \1\.speed == (?:a1|p4)"),
                "两段短路谓词必须共同控制循环退出载荷");
            Assert.IsTrue(Regex.IsMatch(loopGuardedBreakBody,
                    @"(v\d+)\.checked = 1;[\s\S]*break;"),
                "循环退出前的副作用必须保持在 break 之前");
            Assert.AreEqual(1, CountOccurrences(loopGuardedBreakBody, "break;"),
                "带载荷的循环退出块不能在短路守卫合并时丢失 break");
            var switchConditionalReturnBody = SliceBetween(
                result,
                "function validationSwitchConditionalReturnAndContinuation",
                "function validationSwitchGuardedSharedVoid");
            Assert.AreEqual(2, CountOccurrences(switchConditionalReturnBody, "break;"),
                "条件提前返回的两个 case 都必须保留到公共尾部的 break");
            StringAssert.Contains(switchConditionalReturnBody, "return stepLeft();");
            StringAssert.Contains(switchConditionalReturnBody, "return stepRight();");
            Assert.IsTrue(
                switchConditionalReturnBody.LastIndexOf("finishMove();", StringComparison.Ordinal) >
                switchConditionalReturnBody.LastIndexOf("break;", StringComparison.Ordinal),
                "switch 的公共尾部不能被收入首个 case");
            var doWhileHeaderGuardBody = SliceBetween(
                result,
                "function validationDoWhileHeaderGuard",
                "function validationGuardedLeafIncrement");
            StringAssert.Contains(doWhileHeaderGuardBody, "do\r\n");
            StringAssert.Contains(doWhileHeaderGuardBody, "var v7 = p3[p4];");
            StringAssert.Contains(doWhileHeaderGuardBody,
                "if (v7 === void || !isvalid v7 || v7.visible && v7.enabled)");
            StringAssert.Contains(doWhileHeaderGuardBody, "while (p4 != v6);");
            Assert.IsFalse(doWhileHeaderGuardBody.Contains("while (v7 !== void)",
                    StringComparison.Ordinal),
                "循环体首个早退条件不能取代真正的尾部循环条件");
            var loopContinueGuardBody = SliceBetween(
                result,
                "function validationLoopContinueGuardedInnerLoop",
                "function validationPostfixMemberArgument");
            StringAssert.Contains(loopContinueGuardBody,
                "if (typeof p3[v7] == \"String\")");
            StringAssert.Contains(loopContinueGuardBody, "var v8 = p3[v7];");
            StringAssert.Contains(loopContinueGuardBody, "for (var v9 = 0;");
            Assert.IsTrue(
                loopContinueGuardBody.IndexOf("if (typeof", StringComparison.Ordinal) <
                loopContinueGuardBody.IndexOf("var v8", StringComparison.Ordinal) &&
                loopContinueGuardBody.IndexOf("var v8", StringComparison.Ordinal) <
                loopContinueGuardBody.IndexOf("for (var v9", StringComparison.Ordinal),
                "continue 对侧的赋值和子循环必须共同留在类型守卫内");
            Assert.IsFalse(Regex.IsMatch(loopContinueGuardBody,
                    @"(?m)^\s*typeof\s+[^;]+;\s*$"),
                "循环内类型守卫不能退化成裸 typeof 比较");
            Assert.IsFalse(Regex.IsMatch(result,
                    @"\b(?:if|else)[^{\r\n]*\{\r?\n\s*\}"),
                "回归样本不应输出无意义的空 if/else");
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
                StringAssert.Contains(result, "switch (p3.mode) {");
                StringAssert.Contains(result, "try {");
                StringAssert.Contains(result, "catch(v4) {");

                Config.OpeningBraceOnNewLine = true;
                var allman = new Decompiler(path).Decompile();
                StringAssert.Contains(allman, "class SemanticControlFlow\r\n{");
                StringAssert.Contains(allman, "function guard(p3, p4)\r\n    {");
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
            var originalInference = Config.UseInferredVariableNames;
            var originalBraceStyle = Config.OpeningBraceOnNewLine;
            try
            {
                Config.UseLegacyRegisterVariableNames = false;
                Config.UseInferredVariableNames = false;
                Config.OpeningBraceOnNewLine = false;
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
                StringAssert.Contains(sequential, "function defaultArgument(a0 = 1) {");
                StringAssert.Contains(sequential,
                    "function parameterDeclarationOwnership(a0, a1 = a0.value) {");
                var parameterOwnership = SliceBetween(
                    sequential, "function parameterDeclarationOwnership", "function position");
                Assert.IsFalse(Regex.IsMatch(parameterOwnership, @"\bvar\s+a[01]\b"),
                    "参数在签名中已经声明，函数体不能再次输出同名 var");
                StringAssert.Contains(parameterOwnership, "a1 = a0.override;");
                StringAssert.Contains(sequential,
                    "function uninitializedLocals(a0, a1) {\r\n        var v0 = void;\r\n        var v1 = void;");
                var sequentialGuards = SliceBetween(
                    sequential, "function sequentialNestedCallGuards", "function sharedCases");
                StringAssert.Contains(sequentialGuards, "if (a0) {");
                StringAssert.Contains(sequentialGuards, "if (a1) {");
                StringAssert.Contains(sequentialGuards, "a2.comp.processReturn();");
                StringAssert.Contains(sequentialGuards, "if (a2.processReturn()) {");
                StringAssert.Contains(sequentialGuards, "var v0 = showPageBreakAndClear();");
                StringAssert.Contains(sequentialGuards, "else if (v0 == -3)");
                StringAssert.Contains(sequentialGuards, "return fallbackSpeed();");
                Assert.AreEqual(2, CountOccurrences(sequentialGuards, "processReturn()"),
                    "顺序嵌套守卫中的副作调用和条件调用都必须保留");
                Assert.IsFalse(sequentialGuards.Contains("if (a0 && a1)", StringComparison.Ordinal),
                    "带副作的第一个内层 if 不能与外层 gate 合并为短路谓词");
                var configuredFallback = SliceBetween(
                    sequential, "function configuredFallbackChain", "function constructorMember");
                StringAssert.Contains(configuredFallback, "envinfo.layers[a0]");
                Assert.AreEqual(3, CountOccurrences(configuredFallback, "getNewLayer("),
                    "已有对象、已配置对象和名称前缀三条新建路径都必须保留");
                Assert.IsFalse(sequential.Contains("function guard(p3, p4)", StringComparison.Ordinal));
                Assert.IsFalse(sequential.Contains("\r\n\r\n\r\n", StringComparison.Ordinal),
                    "输出中不应出现连续两个以上空白行");
                Assert.IsFalse(Regex.IsMatch(sequential, @"\r\n[ \t]*\r\n[ \t]*}"),
                    "结构语句后的分隔行不应残留在右大括号前");

                Config.UseInferredVariableNames = true;
                var inferred = new Decompiler(path).Decompile();
                StringAssert.Contains(inferred, "var name_0 = void;");
                StringAssert.Contains(inferred, "var name_0 = a0.name;");
                StringAssert.Contains(inferred, "var name_1 = a1.name;");
                StringAssert.Contains(inferred, "consume(name_0, name_1);");
                Assert.IsFalse(inferred.Contains("var name_;", StringComparison.Ordinal));

                Config.UseInferredVariableNames = false;
                Config.UseLegacyRegisterVariableNames = true;
                var legacy = new Decompiler(path).Decompile();
                StringAssert.Contains(legacy, "function guard(p3, p4)");
                StringAssert.Contains(legacy, "for (var v5 = 0; v5 < p4.count; v5++)");
            }
            finally
            {
                Config.UseLegacyRegisterVariableNames = originalStyle;
                Config.UseInferredVariableNames = originalInference;
                Config.OpeningBraceOnNewLine = originalBraceStyle;
            }
        }

        private static int CountExpressionReference(Expression root, Expression target)
        {
            if (root == null)
            {
                return 0;
            }

            var count = ReferenceEquals(root, target) ? 1 : 0;
            if (root.Children == null)
            {
                return count;
            }

            foreach (var child in root.Children.OfType<Expression>())
            {
                count += CountExpressionReference(child, target);
            }

            return count;
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
            var end = endMarker == null
                ? text.Length
                : text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
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
