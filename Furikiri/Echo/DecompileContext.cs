using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    internal class DecompileContext
    {
        public List<DetectHandler> Detector { get; set; }
        public List<Instruction> InstructionQueue { get; set; } = new List<Instruction>();
        public List<ITjsPattern> Blocks { get; set; } = new List<ITjsPattern>();

        public DecompileContext(List<DetectHandler> detectors)
        {
            Detector = detectors;
        }

        public DecompileContext()
        { }
    }
}
