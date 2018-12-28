using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    internal delegate IPattern DetectHandler(List<Instruction> instructions, int index, DecompileContext context);

    public interface IPattern
    {
        /// <summary>
        /// if it's a single line
        /// </summary>
        bool Terminal { get; set; }

        /// <summary>
        /// instruction step
        /// </summary>
        int Length { get; }
    }

    /// <summary>
    /// Displayable pattern
    /// </summary>
    public interface IExpressionPattern : IPattern
    {
        TjsVarType Type { get; }
        short Slot { get; }
        string ToString();
    }

    /// <summary>
    /// Branch
    /// </summary>
    public interface IBranchPattern : IPattern
    {
        string ToString();
    }
}