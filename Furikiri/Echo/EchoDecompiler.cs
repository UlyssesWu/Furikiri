using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Furikiri.Echo.Patterns;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    /// <summary>
    /// A naive TJS decompiler
    /// </summary>
    public class EchoDecompiler
    {
        public Module Script { get; set; }

        public Dictionary<CodeObject, Method> Methods { get; set; } = new Dictionary<CodeObject, Method>();

        internal List<DetectHandler> Detectors = new List<DetectHandler>();
        internal List<DetectHandler> BranchDetectors = new List<DetectHandler>();

        public EchoDecompiler()
        {
            Init();
        }

        public EchoDecompiler(string path)
        {
            Init();
            Script = new Module(path);
        }

        private void Init()
        {
            BranchDetectors.Add(LoopPattern.Match);
            Detectors.Add(RegMemberPattern.Match);
            Detectors.Add(UnaryOpPattern.Match);
            Detectors.Add(BinaryOpPattern.Match);
            Detectors.Add(CallPattern.Match);
            Detectors.Add(DeletePattern.Match);
        }

        public void Decompile()
        {
            if (Script == null)
            {
                return;
            }

            Methods[Script.TopLevel] = Script.TopLevel.ResolveMethod();

            foreach (var obj in Script.Objects)
            {
                if (obj == Script.TopLevel)
                {
                    continue;
                }

                Methods[obj] = obj.ResolveMethod();
            }

            var context = new DecompileContext(Script.TopLevel, Detectors);
            var m = Methods[Script.TopLevel];
            m.Compact();

            int offset = 0;
            while (offset < m.Instructions.Count)
            {
                bool found = false;
                foreach (var branchDetect in BranchDetectors)
                {
                    var result = branchDetect(m.Instructions, offset, context);
                    if (result != null)
                    {
                        context.Blocks.Add(result);
                        offset += result.Length;
                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    continue;
                }

                foreach (var detect in Detectors)
                {
                    var result = detect(m.Instructions, offset, context);
                    if (result != null)
                    {
                        context.Blocks.Add(result);
                        offset += result.Length;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    Debug.WriteLine($"Failed to detect pattern at {m.Name}:L{offset}");
                    context.InstructionQueue.Add(m.Instructions[offset]);
                    offset++;
                }
            }

            var blocks = context.Blocks;
            StringBuilder sb = new StringBuilder();
            foreach (var block in blocks)
            {
                if (block is IExpressionPattern exp && block.Terminal)
                {
                    sb.AppendLine(exp.ToString());
                }
                else if (block is IBranchPattern exp2 && block.Terminal)
                {
                    sb.AppendLine(exp2.ToString());
                }
                if (block is BinaryOpPattern b)
                {
                    var l = b.Left;
                    //var n = (LocalPattern) b.Left;
                    var r = b.Right;
                }
            }

            var text = sb.ToString();
        }
    }
}
