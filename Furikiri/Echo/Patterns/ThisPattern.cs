using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// this / global
    /// </summary>
    class ThisPattern : IExpression
    {
        public bool Terminal
        {
            get => false;
            set { return; }
        }

        public int Length { get; }
        public TjsVarType Type => TjsVarType.Object;
        public short Slot => IsProxy ? Const.ThisProxy : Const.This;
        public bool InGlobal { get; set; }
        public bool IsProxy { get; set; }
        public string Name { get; set; }
        public CodeObject This { get; set; }
        public bool NoDisplay => IsProxy || InGlobal;

        internal ThisPattern(bool inGlobal, bool isProxy = false)
        {
            InGlobal = inGlobal;
            IsProxy = isProxy;
        }

        public override string ToString()
        {
            if (NoDisplay)
            {
                return "";
            }
            if (InGlobal && IsProxy)
            {
                return "global";
            }
            return "this";
        }
    }
}