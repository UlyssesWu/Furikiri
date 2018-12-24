using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Emit;
using Tjs2.Engine;

namespace Furikiri.Echo
{
    internal delegate ITjsPattern DetectHandler(List<Instruction> instructions, int index, DecompileContext context);

    public interface ITjsPattern
    {
        int Length { get; }
    }
}
