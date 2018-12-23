using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        
        public EchoDecompiler()
        { }

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

            //TODO:
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
