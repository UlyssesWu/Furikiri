using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Furikiri.AST.Statements;
using Furikiri.AST.Expressions;
using Furikiri.Echo.Language;
using Furikiri.Echo.Pass;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    /// <summary>
    /// TJS decompiler
    /// </summary>
    public class Decompiler
    {
        public Module Script { get; set; }

        internal Decompiler()
        {
        }

        public Decompiler(string path)
        {
            Script = new Module(path);
        }

        public string Decompile(string objName)
        {
            if (Script == null)
            {
                return "";
            }

            Script.Resolve();

            Dictionary<Method, BlockStatement> methods = new Dictionary<Method, BlockStatement>();

            //methods.Add(Script.Methods[Script.TopLevel], DecompileObject(Script.TopLevel));

            var method = Script.Methods.FirstOrDefault(m => m.Key.Name == objName);
            var block = DecompileObject(method.Key);
            methods.Add(method.Value, block);
            
            var writer = new StringWriter();
            var tjs = new TjsWriter(writer) {MethodRefs = methods};
            tjs.WriteLicense();

            foreach (var m in methods)
            {
                if (m.Key.IsLambda)
                {
                    continue;
                }

                tjs.WriteLine();
                tjs.WriteFunction(m.Key, m.Value);
                tjs.WriteLine();
            }

            writer.Flush();
            var result = writer.ToString();
            return result;
        }

        public string Decompile()
        {
            if (Script == null)
            {
                return "";
            }

            Script.Resolve();

            Dictionary<Method, BlockStatement> methods = new Dictionary<Method, BlockStatement>();
            Dictionary<Property, (BlockStatement Getter, BlockStatement Setter)> propertyBlocks =
                new Dictionary<Property, (BlockStatement Getter, BlockStatement Setter)>();
            var classObjects = Script.Objects.Where(o => o.ContextType == TjsContextType.Class).ToList();
            var classSuperExpressions = new Dictionary<CodeObject, Expression>();
            var classBodies = new Dictionary<CodeObject, BlockStatement>();

            methods.Add(Script.Methods[Script.TopLevel], DecompileObject(Script.TopLevel));
            foreach (var classObject in classObjects)
            {
                classBodies[classObject] = DecompileObject(classObject);
            }
            foreach (var method in Script.Methods)
            {
                if (method.Key == Script.TopLevel)
                {
                    continue;
                }

                switch (method.Key.ContextType)
                {
                    case TjsContextType.PropertyGetter:
                    case TjsContextType.PropertySetter:
                        var propMethodBlock = DecompileObject(method.Key);
                        methods.Add(method.Value, propMethodBlock);
                        break;
                    case TjsContextType.Function:
                    case TjsContextType.ExprFunction:
                    case TjsContextType.TopLevel:
                        var block = DecompileObject(method.Key);
                        methods.Add(method.Value, block);
                        break;
                }
            }

            // Resolve/decompile superclass getter proxies so class declarations can emit extends.
            foreach (var classObj in classObjects)
            {
                if (classObj.SuperClass == null)
                {
                    continue;
                }

                var superBlock = DecompileObject(classObj.SuperClass);
                Expression superExpr = null;
                if (superBlock?.Statements != null)
                {
                    foreach (var statement in superBlock.Statements)
                    {
                        if (statement is ExpressionStatement exprStmt && exprStmt.Expression is ReturnExpression ret &&
                            ret.Return != null)
                        {
                            superExpr = ret.Return;
                            break;
                        }
                    }
                }

                if (superExpr != null)
                {
                    classSuperExpressions[classObj] = superExpr;
                }
            }

            // Build property blocks before class/function emission so class writer can inline
            // class-owned properties into class bodies.
            foreach (var property in Script.Properties.Values)
            {
                BlockStatement getterBlock = null;
                BlockStatement setterBlock = null;
                if (property.Getter != null)
                {
                    methods.TryGetValue(property.Getter, out getterBlock);
                }

                if (property.Setter != null)
                {
                    methods.TryGetValue(property.Setter, out setterBlock);
                }

                propertyBlocks[property] = (getterBlock, setterBlock);
            }

            var writer = new StringWriter();
            var tjs = new TjsWriter(writer)
            {
                MethodRefs = methods,
                PropertyRefs = propertyBlocks,
                ClassSuperExpressions = classSuperExpressions,
                ClassBodies = classBodies
            };
            tjs.WriteLicense();

            var topLevelMethod = Script.Methods[Script.TopLevel];
            // Write classes first so top-level class alias statements can be skipped safely.
            foreach (var classObj in classObjects)
            {
                tjs.WriteLine();
                tjs.WriteClass(classObj);
                tjs.WriteLine();
            }

            // Write normal function declarations first so top-level `delete foo` statements
            // always appear after `function foo(...)` declarations.
            foreach (var m in methods)
            {
                if (m.Key == topLevelMethod || m.Key.IsLambda)
                {
                    continue;
                }

                if (m.Key.Object.ContextType is TjsContextType.PropertyGetter or TjsContextType.PropertySetter)
                {
                    continue;
                }

                if (m.Key.Object.Parent?.ContextType == TjsContextType.Class)
                {
                    continue;
                }

                tjs.WriteLine();
                tjs.WriteFunction(m.Key, m.Value);
                tjs.WriteLine();
            }

            tjs.WriteLine();

            // Then output top-level properties with associated getter/setter bodies.
            foreach (var propertyBlock in propertyBlocks)
            {
                if (propertyBlock.Key.Parent?.ContextType == TjsContextType.Class)
                {
                    continue;
                }

                tjs.WriteProperty(propertyBlock.Key, propertyBlock.Value.Getter, propertyBlock.Value.Setter);
                tjs.WriteLine();
            }

            // Finally write top-level body.
            tjs.WriteLine();
            tjs.WriteFunction(topLevelMethod, methods[topLevelMethod]);
            tjs.WriteLine();

            writer.Flush();
            var result = writer.ToString();
            return result;
        }

        private BlockStatement DecompileObject(CodeObject obj)
        {
            var context = new DecompileContext(obj);
            Method m;
            if (!Script.Methods.TryGetValue(obj, out m))
            {
                m = obj.ResolveMethod();
            }
            m.Compact();
            context.BuildCFG(m.Instructions);
            if (IsDebugDumpEnabled())
            {
                InitDebugDump(obj);
                DumpDebugState(obj, context, "After BuildCFG");
            }
            
            // Pass 1: Register members
            var pass1 = new RegMemberPass();
            var entry = pass1.Process(context, new BlockStatement());
            DumpDebugState(obj, context, "After RegMemberPass");

            // Pass 2: Build expressions (generates Phi nodes)
            var pass2 = new ExpressionPass();
            entry = pass2.Process(context, entry);
            DumpDebugState(obj, context, "After ExpressionPass");

            // Pass 3: Expression propagation and Phi elimination
            var pass3 = new ExpressionPropagationPass();
            entry = pass3.Process(context, entry);
            DumpDebugState(obj, context, "After ExpressionPropagationPass");

            // Pass 4: Control flow analysis
            var pass4 = new ControlFlowPass();
            entry = pass4.Process(context, entry);
            DumpDebugState(obj, context, "After ControlFlowPass");

            // Pass 5: Collect statements
            var pass5 = new StatementCollectPass();
            entry = pass5.Process(context, entry);
            DumpDebugState(obj, context, "After StatementCollectPass");

            m.Vars = context.Vars;
            
            return entry;
        }

        private static bool IsDebugDumpEnabled()
        {
            return Config.DumpDecompileDebug ||
                   string.Equals(Environment.GetEnvironmentVariable("FURIKIRI_DEBUG_DUMP"), "1",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string GetDebugDumpDir()
        {
            var envDir = Environment.GetEnvironmentVariable("FURIKIRI_DEBUG_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                return envDir;
            }

            return Path.Combine(Directory.GetCurrentDirectory(), "decompile-debug");
        }

        private static string GetDebugDumpPath(CodeObject obj)
        {
            var dir = GetDebugDumpDir();
            Directory.CreateDirectory(dir);
            var rawName = string.IsNullOrWhiteSpace(obj?.Name) ? "top-level" : obj.Name;
            if (!string.IsNullOrWhiteSpace(obj?.Parent?.Name))
            {
                rawName = $"{obj.Parent.Name}__{rawName}__{obj.ContextType}";
            }
            // 同一父对象下可能有多个同名匿名函数。对象表序号来自字节码文件且稳定，
            // 用它区分调试文件，避免后反编译的闭包覆盖先前闭包的 CFG。
            var objectIndex = obj?.Script?.Objects?.IndexOf(obj) ?? -1;
            if (objectIndex >= 0)
            {
                rawName = $"{rawName}__obj{objectIndex:D4}";
            }
            var invalid = Path.GetInvalidFileNameChars();
            var safeName = new string(rawName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return Path.Combine(dir, $"{safeName}.debug.txt");
        }

        private static void InitDebugDump(CodeObject obj)
        {
            var path = GetDebugDumpPath(obj);
            var banner = new StringBuilder();
            banner.AppendLine($"Decompiler debug dump for: {obj?.Name ?? "top-level"}");
            banner.AppendLine();
            File.WriteAllText(path, banner.ToString());
        }

        private static void DumpDebugState(CodeObject obj, DecompileContext context, string stage)
        {
            if (!IsDebugDumpEnabled())
            {
                return;
            }

            var path = GetDebugDumpPath(obj);
            File.AppendAllText(path, context.DumpState(stage));
        }
    }
}
