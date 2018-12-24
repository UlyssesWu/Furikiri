﻿using System.Collections.Generic;
using System.Diagnostics;
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
            Detectors.Add(RegMemberPattern.TryMatch);
            Detectors.Add(ChainGetPattern.TryMatch);
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

            var context = new DecompileContext(Detectors);
            var m = Methods[Script.TopLevel];
            int offset = 0;
            while (offset < m.Instructions.Count)
            {
                bool found = false;
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

            var p = context.Blocks;
        }

        private void Compact(Method method)
        {
            method.Resolve();
            //HashSet<OpCode> toBeRemoved = new HashSet<OpCode>();
            method.Instructions.RemoveAll(instruction =>
                instruction.OpCode == OpCode.NOP || instruction.OpCode == OpCode.DEBUGGER);
            method.Merge();
        }
    }
}
