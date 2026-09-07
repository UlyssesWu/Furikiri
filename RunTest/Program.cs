using System.Diagnostics;
using System.Text;
using Furikiri;
using Furikiri.Echo;
using Furikiri.Emit;

namespace RunTest
{
    internal class Program
    {
        private const int CompilerTimeoutMilliseconds = 30_000;

        static int Main(string[] args)
        {
            Config.DumpDecompileDebug = false;

            if (args.Length > 0 && args[0] == "--validate")
            {
                return Validate(args.Skip(1).Select(Path.GetFullPath).ToArray());
            }

            if (args.Length > 0 && args[0] == "--opcode-coverage")
            {
                return ReportOpcodeCoverage(args.Skip(1).Select(Path.GetFullPath).ToArray());
            }

            if (args.Length > 0 && args[0] == "--bytecode-audit")
            {
                return AuditRecompiledBytecode(args.Skip(1).Select(Path.GetFullPath).ToArray());
            }

            if (args.Length == 3 && args[0] == "--decompile-one")
            {
                return DecompileOne(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            }

            if (args.Length == 3 && args[0] == "--disassemble")
            {
                return DisassembleOne(
                    Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
            }

            var defaultPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Furikiri.Tests", "Res", "D3D.tjs.comp"));
            var testPath = args.Length > 0 ? Path.GetFullPath(args[0]) : defaultPath;

            return TestDecompile(testPath) ? 0 : 1;
        }

        private static bool TestDecompile(string path, string func = "")
        {
            try
            {
                var decompiler = new Decompiler(path);
                var result = !string.IsNullOrEmpty(func) ? decompiler.Decompile(func) : decompiler.Decompile();

                var outputPath = "decompiled.tjs";
                File.WriteAllText(outputPath, result);
                Console.WriteLine($"output path: {Path.GetFullPath(outputPath)}");
                return true;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return false;
            }
        }

        private static int Validate(IReadOnlyList<string> inputRoots)
        {
            if (inputRoots.Count == 0)
            {
                Console.Error.WriteLine("Usage: RunTest --validate <compiled-source-root> [more roots ...]");
                return 2;
            }

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var compilerPath = Path.Combine(repositoryRoot, "Furikiri.Tests", "Res", "tjs2Compiler.exe");
            var outputRoot = Path.Combine(repositoryRoot, "bak", "validation-output");
            Directory.CreateDirectory(outputRoot);

            if (!File.Exists(compilerPath))
            {
                Console.Error.WriteLine($"Compiler not found: {compilerPath}");
                return 2;
            }

            var results = new List<ValidationResult>();
            for (var suiteIndex = 0; suiteIndex < inputRoots.Count; suiteIndex++)
            {
                var inputRoot = inputRoots[suiteIndex];
                if (!Directory.Exists(inputRoot))
                {
                    results.Add(new ValidationResult($"suite-{suiteIndex + 1:D2}", inputRoot, "discover", 2, "Directory not found"));
                    continue;
                }

                var suiteName = $"suite-{suiteIndex + 1:D2}-{SanitizeFileName(Path.GetFileName(inputRoot))}";
                var sourceFiles = Directory.EnumerateFiles(inputRoot, "*.tjs.comp", SearchOption.AllDirectories)
                    .Where(path => !path.EndsWith(".decompiled.tjs.comp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                Console.WriteLine($"[{suiteName}] {sourceFiles.Length} files");
                if (sourceFiles.Length == 0)
                {
                    results.Add(new ValidationResult(suiteName, inputRoot, "discover", 2,
                        "No compiled source files found"));
                    continue;
                }
                foreach (var sourcePath in sourceFiles)
                {
                    var relativeSourcePath = Path.GetRelativePath(inputRoot, sourcePath);
                    var originalTjsPath = relativeSourcePath[..^".comp".Length];
                    var outputRelativePath = Path.ChangeExtension(originalTjsPath, ".decompiled.tjs");
                    var outputPath = Path.Combine(outputRoot, suiteName, outputRelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

                    // 控制流恢复处理的输入不受信任，栈溢出等严重错误无法在当前进程中捕获。
                    // 每个文件放到独立子进程，保证一个坏样本不会中止整个验证批次。
                    var decompileResult = RunDecompileChild(sourcePath, outputPath);
                    if (decompileResult.ExitCode != 0)
                    {
                        results.Add(new ValidationResult(suiteName, relativeSourcePath, "decompile", decompileResult.ExitCode, decompileResult.Message));
                        Console.WriteLine($"  FAIL decompile: {relativeSourcePath}");
                        continue;
                    }

                    var compilerResult = RunCompiler(compilerPath, outputPath);
                    results.Add(new ValidationResult(suiteName, relativeSourcePath, "recompile", compilerResult.ExitCode, compilerResult.Message));
                    Console.WriteLine(compilerResult.ExitCode == 0
                        ? $"  PASS: {relativeSourcePath}"
                        : $"  FAIL recompile({compilerResult.ExitCode}): {relativeSourcePath}");
                }
            }

            var reportPath = Path.Combine(outputRoot, "report.csv");
            WriteReport(reportPath, results);
            var failed = results.Count(result => result.ExitCode != 0);
            Console.WriteLine($"Validation finished: {results.Count - failed}/{results.Count} passed; report: {reportPath}");
            return failed == 0 ? 0 : 1;
        }

        private static int ReportOpcodeCoverage(IReadOnlyList<string> inputRoots)
        {
            if (inputRoots.Count == 0)
            {
                Console.Error.WriteLine("Usage: RunTest --opcode-coverage <compiled-source-root> [more roots ...]");
                return 2;
            }

            var counts = new Dictionary<OpCode, long>();
            var fileCount = 0;
            var methodCount = 0;
            foreach (var inputRoot in inputRoots)
            {
                if (!Directory.Exists(inputRoot))
                {
                    Console.Error.WriteLine($"Directory not found: {inputRoot}");
                    return 2;
                }

                foreach (var sourcePath in Directory.EnumerateFiles(inputRoot, "*.tjs.comp", SearchOption.AllDirectories)
                             .Where(path => !path.EndsWith(".decompiled.tjs.comp", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var module = new Module(sourcePath);
                        module.Resolve();
                        fileCount++;
                        methodCount += module.Methods.Count;
                        foreach (var instruction in module.Methods.Values.SelectMany(method => method.Instructions))
                        {
                            counts[instruction.OpCode] = counts.TryGetValue(instruction.OpCode, out var count)
                                ? count + 1
                                : 1;
                        }
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"Cannot inspect {sourcePath}: {FlattenMessage(exception)}");
                        return 1;
                    }
                }
            }

            Console.WriteLine($"Files: {fileCount}; methods: {methodCount}; instructions: {counts.Values.Sum()}");
            foreach (var pair in counts.OrderBy(pair => (short)pair.Key))
            {
                Console.WriteLine($"{(short)pair.Key,3} {pair.Key,-12} {pair.Value,10}");
            }

            return 0;
        }

        private static int AuditRecompiledBytecode(IReadOnlyList<string> inputRoots)
        {
            if (inputRoots.Count == 0)
            {
                Console.Error.WriteLine("Usage: RunTest --bytecode-audit <compiled-source-root> [more roots ...]");
                return 2;
            }

            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            var outputRoot = Path.Combine(repositoryRoot, "bak", "validation-output");
            var rows = new List<BytecodeAuditRow>();
            var fileCount = 0;
            var originalMethodCount = 0;
            var recompiledMethodCount = 0;

            for (var suiteIndex = 0; suiteIndex < inputRoots.Count; suiteIndex++)
            {
                var inputRoot = inputRoots[suiteIndex];
                if (!Directory.Exists(inputRoot))
                {
                    Console.Error.WriteLine($"Directory not found: {inputRoot}");
                    return 2;
                }

                var suiteName = $"suite-{suiteIndex + 1:D2}-{SanitizeFileName(Path.GetFileName(inputRoot))}";
                foreach (var sourcePath in Directory.EnumerateFiles(inputRoot, "*.tjs.comp", SearchOption.AllDirectories)
                             .Where(path => !path.EndsWith(".decompiled.tjs.comp", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var relativeSourcePath = Path.GetRelativePath(inputRoot, sourcePath);
                    var originalTjsPath = relativeSourcePath[..^".comp".Length];
                    var outputRelativePath = Path.ChangeExtension(originalTjsPath, ".decompiled.tjs.comp");
                    var recompiledPath = Path.Combine(outputRoot, suiteName, outputRelativePath);
                    if (!File.Exists(recompiledPath))
                    {
                        Console.Error.WriteLine($"Recompiled bytecode not found: {recompiledPath}");
                        return 1;
                    }

                    try
                    {
                        var original = ReadSemanticSignatures(sourcePath);
                        var recompiled = ReadSemanticSignatures(recompiledPath);
                        fileCount++;
                        originalMethodCount += original.MethodCount;
                        recompiledMethodCount += recompiled.MethodCount;

                        foreach (var methodKey in original.Signals.Keys.Union(recompiled.Signals.Keys)
                                     .OrderBy(key => key, StringComparer.Ordinal))
                        {
                            var left = original.Signals.GetValueOrDefault(methodKey) ?? new Dictionary<string, int>();
                            var right = recompiled.Signals.GetValueOrDefault(methodKey) ?? new Dictionary<string, int>();
                            foreach (var signal in left.Keys.Union(right.Keys).OrderBy(key => key, StringComparer.Ordinal))
                            {
                                var originalCount = left.GetValueOrDefault(signal);
                                var recompiledCount = right.GetValueOrDefault(signal);
                                if (originalCount != recompiledCount)
                                {
                                    rows.Add(new BytecodeAuditRow(
                                        suiteName, relativeSourcePath, methodKey, signal,
                                        originalCount, recompiledCount));
                                }
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"Cannot audit {sourcePath}: {FlattenMessage(exception)}");
                        return 1;
                    }
                }
            }

            var reportPath = Path.Combine(outputRoot, "bytecode-audit.csv");
            WriteBytecodeAuditReport(reportPath, rows);
            var losses = rows.Count(IsHighRiskLoss);
            var unpairedReadLosses = rows.Count(row =>
                IsUnpairedReadLoss(row, rows));
            var dispatchReturnRisks = rows
                .GroupBy(row => (row.Suite, row.File, row.Method))
                .Count(group => IsDispatchReturnShapeRisk(group));
            Console.WriteLine(
                $"Bytecode audit: files={fileCount}, methods={originalMethodCount}->{recompiledMethodCount}, " +
                $"differences={rows.Count}, high-risk losses={losses}, " +
                $"unpaired read losses={unpairedReadLosses}, " +
                $"dispatch-return risks={dispatchReturnRisks}; report: {reportPath}");
            return fileCount == 0 || originalMethodCount != recompiledMethodCount ||
                   losses > 0 || unpairedReadLosses > 0 || dispatchReturnRisks > 0 ? 1 : 0;
        }

        private static SemanticSignatureSet ReadSemanticSignatures(string path)
        {
            var module = new Module(path);
            module.Resolve();
            var groups = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (var pair in module.Methods)
            {
                var methodKey = BuildMethodKey(pair.Key);
                if (!groups.TryGetValue(methodKey, out var signals))
                {
                    signals = new Dictionary<string, int>(StringComparer.Ordinal);
                    groups[methodKey] = signals;
                }

                AddSignal(signals, "method-count");
                foreach (var instruction in GetReachableInstructions(pair.Value.Instructions))
                {
                    foreach (var signal in GetSemanticSignals(instruction))
                    {
                        AddSignal(signals, signal);
                    }
                }
            }

            return new SemanticSignatureSet(module.Methods.Count, groups);
        }

        private static IEnumerable<Instruction> GetReachableInstructions(
            IReadOnlyList<Instruction> instructions)
        {
            if (instructions.Count == 0)
            {
                yield break;
            }

            var reachable = new bool[instructions.Count];
            var pending = new Stack<int>();
            pending.Push(0);
            while (pending.Count > 0)
            {
                var index = pending.Pop();
                if (index < 0 || index >= instructions.Count || reachable[index])
                {
                    continue;
                }

                reachable[index] = true;
                var instruction = instructions[index];
                var branchTarget = instruction.BranchTarget;
                if (branchTarget != null)
                {
                    pending.Push(branchTarget.Line);
                }

                // JMP、RET 与 THROW 没有正常落空边；条件跳转和 ENTRY 的
                // 正常路径则继续到下一条指令。
                if (instruction.OpCode is not (OpCode.JMP or OpCode.RET or OpCode.THROW))
                {
                    pending.Push(index + 1);
                }
            }

            for (var index = 0; index < instructions.Count; index++)
            {
                if (reachable[index])
                {
                    yield return instructions[index];
                }
            }
        }

        private static string BuildMethodKey(CodeObject method)
        {
            var parts = new Stack<string>();
            for (var current = method; current != null; current = current.Parent)
            {
                parts.Push(BuildObjectPathSegment(current));
            }

            return $"{string.Join('/', parts)}[args={method.FuncDeclArgCount}]";
        }

        private static string BuildObjectPathSegment(CodeObject current)
        {
            var name = current.Name ?? "<anonymous>";
            var baseSegment = $"{current.ContextType}:{name}";
            var objects = current.Script?.Objects;
            if (objects == null)
            {
                return baseSegment;
            }

            // 同一父对象中可能有多个同名闭包或重复方法定义。若仅按名称聚合，
            // 一个对象丢失的效应可能被另一个对象新增的效应抵消。兄弟序号只在
            // 确实重名时加入，普通方法路径仍保持简洁。
            var siblings = objects.Where(candidate =>
                    candidate.Parent == current.Parent &&
                    candidate.ContextType == current.ContextType &&
                    string.Equals(candidate.Name, current.Name, StringComparison.Ordinal) &&
                    candidate.FuncDeclArgCount == current.FuncDeclArgCount)
                .ToList();
            if (siblings.Count <= 1)
            {
                return baseSegment;
            }

            var ordinal = siblings.IndexOf(current);
            return ordinal >= 0 ? $"{baseSegment}#{ordinal}" : baseSegment;
        }

        private static IEnumerable<string> GetSemanticSignals(Instruction instruction)
        {
            var literal = GetResolvedLiteral(instruction);
            switch (instruction.OpCode)
            {
                case OpCode.CALLD:
                    yield return "effect:call";
                    yield return $"call:{literal ?? "<direct>"}";
                    break;
                case OpCode.CALLI:
                    yield return "effect:call";
                    yield return "call:<dynamic>";
                    break;
                case OpCode.CALL:
                    yield return "effect:call";
                    yield return "call:<value>";
                    break;
                case OpCode.NEW:
                    yield return "new";
                    break;
                case OpCode.GPD:
                case OpCode.GPDS:
                    yield return "effect:read";
                    yield return $"read:{literal ?? "<direct>"}";
                    break;
                case OpCode.GPI:
                case OpCode.GPIS:
                    yield return "effect:read";
                    yield return "read:<dynamic>";
                    break;
                case OpCode.SPD:
                case OpCode.SPDE:
                case OpCode.SPDEH:
                case OpCode.SPDS:
                case OpCode.LORPD:
                case OpCode.LANDPD:
                case OpCode.BORPD:
                case OpCode.BXORPD:
                case OpCode.BANDPD:
                case OpCode.SARPD:
                case OpCode.SALPD:
                case OpCode.SRPD:
                case OpCode.ADDPD:
                case OpCode.SUBPD:
                case OpCode.MODPD:
                case OpCode.DIVPD:
                case OpCode.IDIVPD:
                case OpCode.MULPD:
                case OpCode.INCPD:
                case OpCode.DECPD:
                    yield return "effect:write";
                    yield return $"write:{literal ?? "<direct>"}";
                    break;
                case OpCode.SPI:
                case OpCode.SPIE:
                case OpCode.SPIS:
                case OpCode.LORPI:
                case OpCode.LANDPI:
                case OpCode.BORPI:
                case OpCode.BXORPI:
                case OpCode.BANDPI:
                case OpCode.SARPI:
                case OpCode.SALPI:
                case OpCode.SRPI:
                case OpCode.ADDPI:
                case OpCode.SUBPI:
                case OpCode.MODPI:
                case OpCode.DIVPI:
                case OpCode.IDIVPI:
                case OpCode.MULPI:
                case OpCode.INCPI:
                case OpCode.DECPI:
                    yield return "effect:write";
                    yield return "write:<dynamic>";
                    break;
                case OpCode.SETP:
                    yield return "property:set";
                    break;
                case OpCode.GETP:
                    yield return "property:get";
                    break;
                case OpCode.DELD:
                    yield return "effect:delete";
                    yield return $"delete:{literal ?? "<direct>"}";
                    break;
                case OpCode.DELI:
                    yield return "effect:delete";
                    yield return "delete:<dynamic>";
                    break;
                case OpCode.ENTRY:
                    yield return "exception:try";
                    break;
                case OpCode.THROW:
                    yield return "exception:throw";
                    break;
                case OpCode.SRV:
                    // %0 表示 void 结果；其他槽表示显式带值返回。
                    yield return instruction.ToString().EndsWith("%0", StringComparison.Ordinal)
                        ? "return:void"
                        : "return:value";
                    break;
                case OpCode.TYPEOF:
                case OpCode.TYPEOFD:
                case OpCode.TYPEOFI:
                case OpCode.EVAL:
                case OpCode.EEXP:
                case OpCode.CHKINS:
                case OpCode.ASC:
                case OpCode.CHR:
                case OpCode.INV:
                case OpCode.CHKINV:
                case OpCode.CHGTHIS:
                case OpCode.ADDCI:
                    yield return $"operator:{instruction.OpCode}";
                    break;
            }
        }

        private static string? GetResolvedLiteral(Instruction instruction)
        {
            var comment = instruction.Data?.Comment;
            if (string.IsNullOrWhiteSpace(comment))
            {
                return null;
            }

            var separator = comment.IndexOf(" = ", StringComparison.Ordinal);
            return separator >= 0 ? comment[(separator + 3)..] : comment;
        }

        private static void AddSignal(Dictionary<string, int> signals, string signal)
        {
            signals[signal] = signals.GetValueOrDefault(signal) + 1;
        }

        private static bool IsHighRiskLoss(BytecodeAuditRow row)
        {
            if (row.Original <= 0 || row.Recompiled != 0)
            {
                return false;
            }

            // 次数下降可能只是公共尾部提取、不可达代码删除或 CALL/CALLD 形式
            // 改写；报告仍保留该差异，但不把它自动定性为语义丢失。只有某项
            // 具名副作用或整类关键效应从方法签名中完全消失时才列为高风险。
            var signal = row.Signal;
            if (signal.StartsWith("effect:", StringComparison.Ordinal) ||
                signal.StartsWith("exception:", StringComparison.Ordinal) ||
                signal is "new" or "property:set" or "operator:EVAL" or "operator:EEXP")
            {
                return true;
            }

            return IsNamedEffect(signal, "call:") ||
                   IsNamedEffect(signal, "write:") ||
                   IsNamedEffect(signal, "delete:");
        }

        private static bool IsNamedEffect(string signal, string prefix) =>
            signal.StartsWith(prefix, StringComparison.Ordinal) &&
            !signal.AsSpan(prefix.Length).StartsWith("<", StringComparison.Ordinal);

        private static bool IsUnpairedReadLoss(
            BytecodeAuditRow row, IReadOnlyCollection<BytecodeAuditRow> rows)
        {
            if (row.Original <= 0 || row.Recompiled != 0 ||
                !row.Signal.StartsWith("read:", StringComparison.Ordinal))
            {
                return false;
            }

            // GPD + CALL 常会被重编译器融合成 CALLD。只有同一方法中出现
            // 对应调用增量时，才把消失的读取视为这种指令形式变化；其余读取
            // 即使没有写入副作用，也必须回到源码核对接收者链和条件守卫。
            var callSignal = "call:" + row.Signal["read:".Length..];
            return !rows.Any(candidate =>
                candidate.Suite == row.Suite &&
                candidate.File == row.File &&
                candidate.Method == row.Method &&
                candidate.Signal == callSignal &&
                   candidate.Recompiled > candidate.Original);
        }

        /// <summary>
        /// `switch(typeof value)` 的共享 return 正文若被最后一个 case 的局部
        /// 叶子恢复吞掉，重编译结果通常同时出现额外无值返回和重复 TYPEOF。
        /// 单看调用静态次数无法发现该问题，因为剩余 case 仍保留同一个调用点。
        /// </summary>
        private static bool IsDispatchReturnShapeRisk(
            IEnumerable<BytecodeAuditRow> methodRows)
        {
            var rows = methodRows.ToList();
            return rows.Any(row => row.Signal == "return:void" &&
                                   row.Recompiled > row.Original) &&
                   rows.Any(row => row.Signal.StartsWith(
                                           "operator:TYPEOF",
                                           StringComparison.Ordinal) &&
                                   row.Recompiled > row.Original);
        }

        private static void WriteBytecodeAuditReport(string reportPath, IEnumerable<BytecodeAuditRow> rows)
        {
            using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(true));
            writer.WriteLine("suite,file,method,signal,original,recompiled,delta");
            foreach (var row in rows)
            {
                writer.WriteLine(string.Join(',', new[]
                {
                    Csv(row.Suite), Csv(row.File), Csv(row.Method), Csv(row.Signal),
                    row.Original.ToString(), row.Recompiled.ToString(),
                    (row.Recompiled - row.Original).ToString()
                }));
            }
        }

        private static int DecompileOne(string sourcePath, string outputPath)
        {
            try
            {
                var decompiled = new Decompiler(sourcePath).Decompile();
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, decompiled, new UTF8Encoding(false));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static int DisassembleOne(string sourcePath, string outputPath)
        {
            try
            {
                var assembler = new Assembler { AssembleMode = true };
                var text = assembler.Disassemble(sourcePath);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, text, new UTF8Encoding(false));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }

        private static CompilerResult RunDecompileChild(string sourcePath, string outputPath)
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return new CompilerResult(125, "Cannot resolve current executable path");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add("--decompile-one");
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add(outputPath);
            return RunProcess(startInfo, CompilerTimeoutMilliseconds);
        }

        private static CompilerResult RunCompiler(string compilerPath, string sourcePath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = compilerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add(sourcePath);
            startInfo.ArgumentList.Add("c");
            startInfo.ArgumentList.Add("UTF-8");
            return RunProcess(startInfo, CompilerTimeoutMilliseconds);
        }

        private static CompilerResult RunProcess(ProcessStartInfo startInfo, int timeoutMilliseconds)
        {
            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    return new CompilerResult(124, "Compiler timeout");
                }

                Task.WaitAll(stdoutTask, stderrTask);
                var message = string.Join(" | ", new[] { stdoutTask.Result, stderrTask.Result }
                    .Select(text => text.Trim())
                    .Where(text => text.Length > 0));
                if (message.Length > 8_000)
                {
                    message = message[..8_000] + " ...[truncated]";
                }
                return new CompilerResult(process.ExitCode, message);
            }
            catch (Exception exception)
            {
                return new CompilerResult(125, FlattenMessage(exception));
            }
        }

        private static void WriteReport(string reportPath, IEnumerable<ValidationResult> results)
        {
            using var writer = new StreamWriter(reportPath, false, new UTF8Encoding(true));
            writer.WriteLine("suite,file,stage,exit_code,message");
            foreach (var result in results)
            {
                writer.WriteLine(string.Join(',', new[]
                {
                    Csv(result.Suite), Csv(result.File), Csv(result.Stage), result.ExitCode.ToString(), Csv(result.Message)
                }));
            }
        }

        private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

        private static string FlattenMessage(Exception exception) =>
            exception.ToString().Replace("\r", " ").Replace("\n", " | ");

        private static string SanitizeFileName(string name)
        {
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalidCharacter, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "root" : name;
        }

        private sealed record ValidationResult(string Suite, string File, string Stage, int ExitCode, string Message);
        private sealed record CompilerResult(int ExitCode, string Message);
        private sealed record SemanticSignatureSet(
            int MethodCount, Dictionary<string, Dictionary<string, int>> Signals);
        private sealed record BytecodeAuditRow(
            string Suite, string File, string Method, string Signal, int Original, int Recompiled);
    }
}
