using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    internal delegate IPattern DetectHandler(List<Instruction> instructions, int index, DecompileContext context);

    public interface IPattern
    {
        int Length { get; }
    }

    /// <summary>
    /// Displayable pattern
    /// </summary>
    public interface IExpressionPattern : IPattern
    {
        TjsVarType Type { get; }
        int Slot { get; }
        string ToString();
    }
}
