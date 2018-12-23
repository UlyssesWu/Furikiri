using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        public List<Func<List<Instruction>, int, ITjsPattern>> Detectors = new List<Func<List<Instruction>, int, ITjsPattern>>();

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

            var patternList = new List<ITjsPattern>();
            var m = Methods[Script.TopLevel];
            int offset = 0;
            while (offset < m.Instructions.Count)
            {
                bool found = false;
                foreach (var detect in Detectors)
                {
                    var result = detect(m.Instructions, offset);
                    if (result != null)
                    {
                        patternList.Add(result);
                        offset += result.Length;
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    Debug.WriteLine($"Failed to detect pattern at {m.Name}:L{offset}");
                    offset++;
                }
            }

            var p = patternList;
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
