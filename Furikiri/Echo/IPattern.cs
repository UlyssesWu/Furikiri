using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo
{
    internal delegate IPattern DetectHandler(List<Instruction> instructions, int index, DecompileContext context);

    /// <summary>
    /// Statement
    /// </summary>
    internal interface IPattern
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
    internal interface IExpression : IPattern
    {
        TjsVarType Type { get; }
        short Slot { get; }
        string ToString();
    }

    /// <summary>
    /// Branch
    /// </summary>
    internal interface IBranch : IPattern
    {
        BranchType BranchType { get; }
        IExpression Condition { get; }
        string ToString();
    }
}