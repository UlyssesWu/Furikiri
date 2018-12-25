using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>!a</example>
    /// </summary>
    class UnaryOpPattern : IExpressionPattern
    {
        public int Length => 1;
        public bool Terminal { get; set; }
        public TjsVarType Type { get; }
        public short Slot { get; }
    }
}
