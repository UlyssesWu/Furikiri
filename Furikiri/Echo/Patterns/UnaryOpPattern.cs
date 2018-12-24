using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>!a</example>
    /// </summary>
    class UnaryOpPattern : IExpressionPattern
    {
        public int Length => 1;
        public TjsVarType Type { get; }
        public int Slot { get; }
    }
}
