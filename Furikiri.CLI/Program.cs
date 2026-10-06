using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Furikiri.Echo;
using Furikiri.Emit;
using McMaster.Extensions.CommandLineUtils;

namespace Furikiri.CLI
{
    class Program
    {
        static int Main(string[] args)
        {
            Console.WriteLine("Furikiri TJS2 Disassembler/Decompiler");
            Console.WriteLine("by Ulysses, wdwxy12345@gmail.com");
            Console.WriteLine();

            var app = new CommandLineApplication
            {
                Name = "Furikiri",
                Description = "Decompile TJS2 bytecode by default, or use the disasm command to disassemble it.",
                OptionsComparison = StringComparison.OrdinalIgnoreCase,
                ExtendedHelpText = @"Examples:
  Furikiri init.tjs
  Furikiri decompile scripts
  Furikiri disasm init.tjs
  Furikiri disasm scripts --recursive
  Furikiri scripts --recursive --output decompiled
  Furikiri scripts other.tjs --print

Directories are scanned for TJS2 bytecode regardless of extension.
Use --recursive to include subdirectories. Text source and generated output are skipped.
With --output, paths are relative to each input folder and decompiled filenames are unchanged.
Disassembly output uses .tjsasm. Without --output, decompiled files use .dec.tjs."
            };
            app.HelpOption(inherited: true);

            var optDec = app.Option("-d|--dec", "Decompile bytecode (the default; retained for compatibility)",
                CommandOptionType.NoValue, inherited: true);
            var optPrint = app.Option("-p|--print", "Print results instead of writing files",
                CommandOptionType.NoValue, inherited: true);
            var optRecursive = app.Option("-r|--recursive", "Include subdirectories when processing folders",
                CommandOptionType.NoValue, inherited: true);
            var optOutput = app.Option("-o|--output <directory>",
                "Write to this directory, preserving relative paths and decompiled filenames",
                CommandOptionType.SingleValue, inherited: true);
            var optSameLineBrace = app.Option("--same-line-brace",
                "Place opening braces on the same line (the default; retained for compatibility)",
                CommandOptionType.NoValue, inherited: true);
            var optNewLineBrace = app.Option("--new-line-brace", "Place opening braces on a new line",
                CommandOptionType.NoValue, inherited: true);
            var optLegacyRegisterNames = app.Option("--legacy-register-names",
                "Use legacy VM register based names such as p3 and v5",
                CommandOptionType.NoValue, inherited: true);
            var optInferVariableNames = app.Option("--infer-variable-names",
                "Infer readable local names such as name_0 from member accesses",
                CommandOptionType.NoValue, inherited: true);

            void ConfigureCommand(CommandLineApplication command, bool disassemble)
            {
                var paths = command.Argument("Paths", "Bytecode files or folders", multipleValues: true);
                command.OnExecute(() =>
                {
                    if (disassemble && optDec.HasValue())
                    {
                        Console.Error.WriteLine("--dec cannot be combined with disasm.");
                        return 1;
                    }

                    if (optOutput.HasValue() && (optPrint.HasValue() || string.IsNullOrWhiteSpace(optOutput.Value())))
                    {
                        Console.Error.WriteLine("--output requires a directory and cannot be combined with --print.");
                        return 1;
                    }

                    if (paths.Values.Count == 0)
                    {
                        command.ShowHelp();
                        return command == app ? 0 : 1;
                    }

                    Config.OpeningBraceOnNewLine = optNewLineBrace.HasValue() && !optSameLineBrace.HasValue();
                    Config.UseLegacyRegisterVariableNames = optLegacyRegisterNames.HasValue();
                    Config.UseInferredVariableNames = optInferVariableNames.HasValue();
                    return ProcessPaths(paths.Values, disassemble, optPrint.HasValue(), optRecursive.HasValue(),
                        optOutput.Value());
                });
            }

            ConfigureCommand(app, false);
            app.Command("decompile", command =>
            {
                command.Description = "Decompile bytecode files or folders to TJS source";
                ConfigureCommand(command, false);
            });
            app.Command("disasm", command =>
            {
                command.Description = "Disassemble bytecode files or folders to TJS assembly";
                ConfigureCommand(command, true);
            });

            try
            {
                return app.Execute(args);
            }
            catch (CommandParsingException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine("Use --help for usage.");
                return 1;
            }
        }

        private static int ProcessPaths(IEnumerable<string> paths, bool disassemble, bool print, bool recursive,
            string outputDirectory)
        {
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var files = new Dictionary<string, string>(comparer);
            var failed = 0;

            if (outputDirectory != null)
            {
                try
                {
                    outputDirectory = Path.GetFullPath(outputDirectory);
                    if (File.Exists(outputDirectory))
                        throw new IOException("Output directory is an existing file.");
                }
                catch (Exception e)
                {
                    ReportFailure(outputDirectory, e);
                    return 1;
                }
            }

            // Collect inputs before writing output, so generated files cannot enter the batch.
            foreach (var path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        var root = Path.GetFullPath(path);
                        var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        var candidates = Directory.GetFiles(root, "*", search).OrderBy(p => p, comparer);
                        foreach (var candidate in candidates)
                        {
                            try
                            {
                                // A nested output tree may contain results from an earlier run.
                                if (outputDirectory != null && IsInsideDirectory(outputDirectory, root) &&
                                    IsInsideDirectory(candidate, outputDirectory)) continue;
                                if (IsBytecode(candidate)) files.TryAdd(candidate, root);
                            }
                            catch (Exception e)
                            {
                                ReportFailure(candidate, e);
                                failed++;
                            }
                        }
                    }
                    else if (File.Exists(path))
                    {
                        var file = Path.GetFullPath(path);
                        files.TryAdd(file, Path.GetDirectoryName(file));
                    }
                    else
                    {
                        throw new FileNotFoundException("File or directory does not exist.", path);
                    }
                }
                catch (Exception e)
                {
                    ReportFailure(path, e);
                    failed++;
                }
            }

            var succeeded = 0;
            var outputs = files.ToDictionary(file => file.Key,
                file => GetOutputPath(file.Key, disassemble, file.Value, outputDirectory), comparer);
            var conflicts = new HashSet<string>(outputs.Values.GroupBy(output => output, comparer)
                .Where(group => group.Count() > 1).Select(group => group.Key), comparer);
            foreach (var file in files.Keys)
            {
                Console.WriteLine("// File: " + file);
                try
                {
                    var output = outputs[file];
                    if (!print && (files.ContainsKey(output) || conflicts.Contains(output)))
                    {
                        throw new IOException("Output path conflicts with another input or output: " + output);
                    }

                    var result = disassemble
                        ? new Assembler().Disassemble(new Module(file))
                        : new Decompiler(file).Decompile();
                    if (print)
                    {
                        Console.WriteLine(result);
                        Console.WriteLine();
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        File.WriteAllText(output, result);
                        Console.WriteLine("Written: " + output);
                    }
                    succeeded++;
                }
                catch (Exception e)
                {
                    ReportFailure(file, e);
                    failed++;
                }
            }

            if (files.Count == 0 && failed == 0)
            {
                Console.Error.WriteLine("No TJS2 bytecode files found.");
                return 1;
            }

            Console.WriteLine($"Done: {succeeded} succeeded, {failed} failed.");
            return failed == 0 ? 0 : 1;
        }

        private static bool IsBytecode(string path)
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 'T' && stream.ReadByte() == 'J' &&
                   stream.ReadByte() == 'S' && stream.ReadByte() == '2';
        }

        private static string GetOutputPath(string path, bool disassemble, string inputRoot, string outputDirectory)
        {
            if (outputDirectory != null)
            {
                var relativePath = Path.GetRelativePath(inputRoot, path);
                if (disassemble) relativePath = Path.ChangeExtension(relativePath, ".tjsasm");
                return Path.GetFullPath(Path.Combine(outputDirectory, relativePath));
            }

            if (disassemble) return Path.ChangeExtension(path, ".tjsasm");
            // Preserve other extensions so foo.tjs.comp cannot overwrite foo.tjs source.
            return string.Equals(Path.GetExtension(path), ".tjs", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(path, ".dec.tjs")
                : path + ".dec.tjs";
        }

        private static bool IsInsideDirectory(string path, string directory)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            path = Path.TrimEndingDirectorySeparator(path);
            directory = Path.TrimEndingDirectorySeparator(directory);
            if (string.Equals(path, directory, comparison)) return false;
            var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, comparison);
        }

        private static void ReportFailure(string path, Exception e)
        {
            Console.Error.WriteLine($"Failed: {path}: {e.Message}");
        }
    }
}
