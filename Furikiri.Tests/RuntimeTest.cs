using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Furikiri.AST.Expressions;
using Furikiri.Echo;
using Furikiri.Emit;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Furikiri.Tests;

[TestClass]
[DoNotParallelize]
public class RuntimeTest
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", ".."));

    [TestMethod]
    public void GamePatternsPreserveArgumentForwardingAndTopLevelScope()
    {
        AssertRuntimeRoundTrip("GamePatternsRuntime.tjs",
            "fixed:B:C;A:B:C;D:B:C;dynamic:B:C;M:B:C;S:B:C;scope:B:C;G;42:42;O7;C7;5:-:v:9:4:9:obj;undefined:1:0:undefined:undefined:1;QAB:QCD;6BA:9;12:1p3:1pn:0;F:AB:B:CD:D:E:F;3:0;HaHbXY:XY:Ha;image:Ymargin:image:Nmargin;ACCACCPECR;visibility:0;resource:1::1:0:1::LLLCL:42;boolean:0;groups:found:yes;missing:no;;prepare:text0text1;obj0obj1;null0null1;01;;iterations:LCV;checkpoint:SEESESE:0;loop-checkpoint:PSEPEPSEPSETI;numbers::1:T:H:K:1W:1KW:1K2H3T4W5K6H7T8:;swap:7:3:LGRGLSRS:3:7:LGRGLSRS:3:LGLGLSLS;fallthrough:0");
    }

    [TestMethod]
    public void SharedConditionalBodiesPreservePositionAndShortCircuitUpdates()
    {
        const string values = "Dx:-200:0:0;x:200:0:0;Dzoom:150:0:0;DAVIDhelloD";
        var results = new List<string> { values, values, "DAVI", "VI" };
        for (var mask = 0; mask < 32; mask++)
        {
            var trace = "";
            bool Probe(string label, bool value) { trace += label; return value; }
            var result = "";
            for (var n = 0; n < 2; n++)
            {
                if ((Probe("A", (mask & 1) != 0) && Probe("B", (mask & 2) != 0)) ||
                    (Probe("C", (mask & 4) != 0) && Probe("D", (mask & 8) != 0)))
                {
                    if ((mask & 16) != 0 || n == 0) result += "P";
                    result += "T";
                }
            }
            results.Add(trace + ":" + result);
        }
        AssertRuntimeRoundTrip("PositionRuntime.tjs", string.Join("|", results));
    }

    [TestMethod]
    public void SharedReturnDecisionsPreserveFallbackAndShortCircuitEffects()
    {
        var results = new List<string>();
        for (var present = 0; present < 2; present++)
        foreach (var ext in new[] { ".AMV", ".PSB", ".BMB", ".MTN", "" })
        for (var mask = 0; mask < 64; mask++)
        {
            var trace = "";
            bool Probe(string label, bool value) { trace += label; return value; }
            var alternate = (mask & 4) != 0 ? "still" : "";
            var result = "missing";
            if (present != 0)
            {
                result = alternate;
                if (((mask & 1) != 0 && (mask & 2) == 0) || alternate == "")
                {
                    var exists = (mask & 8) != 0;
                    var motion = (mask & 16) != 0;
                    if (!exists) trace += "W";
                    if ((ext != ".PSB" && ext != ".BMB" && ext != ".MTN") ||
                        (ext == ".MTN" && exists && Probe("M", motion)) ||
                        (ext == ".PSB" && exists && Probe("P", motion)) ||
                        (ext == ".BMB" && exists && Probe("B", (mask & 32) != 0)))
                        result = "animated";
                }
            }
            results.Add(trace + ":" + result);
        }
        AssertRuntimeRoundTrip("ImageReturnRuntime.tjs", string.Join("|", results));
    }

    [TestMethod]
    public void NestedLoopGuardsPreserveExitAndAssignmentEffects()
    {
        var results = new List<string>();
        foreach (var input in new[] { "Z", ".", "d", "c", "", "X" })
        for (var limit = 0; limit < 4; limit++)
        {
            var left = "abcd";
            var right = input;
            var previous = -1;
            int? found = null;
            var count = 0;
            var savedLeft = left;
            var savedRight = right;
            for (var line = 0; line < 2; line++)
            {
                if (line < 1 && right != "")
                {
                    while (left.Length > 0 &&
                           (found = ".dcbaX".IndexOf(right[0])) >= 0 && previous != found)
                    {
                        var soft = ".dc".IndexOf(right[0]) >= 0;
                        if (soft && ++count >= limit)
                        {
                            left = savedLeft;
                            right = savedRight;
                            break;
                        }
                        right = left[^1] + right;
                        left = left[..^1];
                        previous = found.Value;
                        if (!soft) { savedLeft = left; savedRight = right; }
                    }
                }
                left += "X";
            }
            results.Add($"{left}:{right}:{previous}:{found}:{count}");
        }
        AssertRuntimeRoundTrip("TextWrapRuntime.tjs", string.Join("|", results));
    }

    [TestMethod]
    public void NestedNamedFunctionsPreserveScopeAndDefaults()
    {
        AssertRuntimeRoundTrip("NestedFunctionRuntime.tjs", "12:13;40:50;17:70:undefined;42;42:10:15;31:7");
    }

    [TestMethod]
    public void NumericConstantsPreserveRuntimeTypesAndValues()
    {
        AssertRuntimeRoundTrip("NumericRuntime.tjs",
            "Real|Real|+Infinity|-Infinity|9007199254740992|9007199254740993|" +
            "9223372036854775807|-9223372036854775808|Integer|NaN|+Infinity|-Infinity");
    }

    [TestMethod]
    public void ConstantIdentityPreservesZeroSignAndWideIntegerBits()
    {
        Assert.IsFalse(ExpressionStructuralComparer.AreEquivalent(
            new ConstantExpression(new TjsReal(0.0)),
            new ConstantExpression(new TjsReal(-0.0))));
        Assert.IsFalse(ExpressionStructuralComparer.AreEquivalent(
            new ConstantExpression(new TjsReal(9007199254740992L)),
            new ConstantExpression(new TjsReal(9007199254740993L))));
        Assert.IsFalse(ExpressionStructuralComparer.AreEquivalent(
            new ConstantExpression(new TjsInt(9007199254740992L)),
            new ConstantExpression(new TjsInt(9007199254740993L))));
        Assert.AreEqual(TjsVarType.Int, new TjsReal(9007199254740993L).Type);
        Assert.AreEqual((object)9007199254740993L, new TjsReal(9007199254740993L).Value);
    }

    [TestMethod]
    public void PropertyOperationsPreserveGetterAndSetterOrder()
    {
        AssertRuntimeRoundTrip("PropertyRuntime.tjs",
            "S3;G;T;S5;A5;G;S13;G;C27;G;G;G;I;N1;J;W42,7,8,9;J;J;L42,7,8,9,0,1,2;Q;J;R7;V10,1;");
    }

    [TestMethod]
    public void ControlFlowPreservesBranchAndExceptionTraces()
    {
        var results = new List<string>();
        for (var mask = 0; mask < 16; mask++)
        {
            var trace = "";
            bool Probe(string label, bool value) { trace += label; return value; }
            var success = ((mask & 1) != 0 && Probe("B", (mask & 2) != 0)) ||
                          ((mask & 4) != 0 && Probe("D", (mask & 8) != 0));
            results.Add(trace + (success ? "T" : "F") + "Z");
        }
        for (var limit = 0; limit < 9; limit++)
        for (var stop = -1; stop < 9; stop++)
        {
            var trace = "";
            var total = 0;
            for (var i = 0; i < limit && i != stop; i++)
            {
                if (i % 2 == 0) trace += "E";
                else
                {
                    trace += i % 3 == 2 ? "D" : i > 4 ? "H" : "L";
                    total += i;
                }
            }
            results.Add(trace + ":" + total);
        }
        results.AddRange(new[] { "A:R", "AC:X", "ABD:N" });
        for (var limit = 0; limit < 5; limit++)
        for (var gate = 0; gate < 2; gate++)
        {
            var trace = "";
            var count = 0;
            while (true)
            {
                trace += "I";
                count++;
                if (count >= limit) break;
                trace += "P";
                if (gate == 0) break;
            }
            results.Add(trace + ":" + count);
        }
        AssertRuntimeRoundTrip("ControlFlowRuntime.tjs", string.Join("|", results));
    }

    internal static void AssertRuntimeRoundTrip(string fixture, string expected)
    {
        var directory = Path.Combine(Root, "bak", "runtime-tests",
            Path.GetFileNameWithoutExtension(fixture));
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, fixture);
        File.Copy(Path.Combine(Root, "Furikiri.Tests", "Res", fixture), source, true);

        // 源码、原字节码、反编译源码和重编译字节码各用独立进程执行，
        // 避免全局变量/类注册污染下一次结果；期望值同时约束测试编译器自身。
        Assert.AreEqual(expected, Execute(source, "r"), "原源码执行结果");
        Execute(source, "c");
        Assert.AreEqual(expected, Execute(source + ".comp", "b"), "原字节码执行结果");
        var output = Path.Combine(directory, "decompiled.tjs");
        var oldCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            File.WriteAllText(output, new Decompiler(source + ".comp").Decompile(),
                new UTF8Encoding(false));
        }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        Assert.AreEqual(expected, Execute(output, "r"), "反编译源码执行结果");
        Execute(output, "c");
        Assert.AreEqual(expected, Execute(output + ".comp", "b"), "重编译字节码执行结果");
    }

    private static string Execute(string path, string mode)
    {
        var start = new ProcessStartInfo(Path.Combine(Root, "Furikiri.Tests", "Res", "tjs2Compiler.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add("Auto");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(true);
            process.WaitForExit();
            Assert.Fail($"TJS2 执行超时: {mode} {path}");
        }
        var diagnostic = stderr.GetAwaiter().GetResult();
        Assert.AreEqual(0, process.ExitCode, $"{mode} {path}\n{stdout.GetAwaiter().GetResult()}\n{diagnostic}");
        if (mode == "c") return "";
        const string marker = "[Validation Result] ";
        var position = diagnostic.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, position, diagnostic);
        return diagnostic[(position + marker.Length)..].TrimEnd('\r', '\n');
    }
}
