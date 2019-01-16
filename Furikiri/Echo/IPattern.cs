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

    internal interface ITerminal : IPattern
    {
        /// <summary>
        /// if it's a single line
        /// </summary>
        bool Terminal { get; set; }

        /// <summary>
        /// Def
        /// </summary>
        HashSet<int> Write { get; set; }

        /// <summary>
        /// Use
        /// </summary>
        HashSet<int> Read { get; set; }

        /// <summary>
        /// variables alive before this
        /// </summary>
        HashSet<int> LiveIn { get; set; }

        /// <summary>
        /// variables alive after this
        /// </summary>
        HashSet<int> LiveOut { get; set; }

        void ComputeUseDefs();
    }
}