using System;

namespace Furikiri.Emit
{
    public class Variable
    {
        public short Slot { get; set; }
        public string Name { get; set; }
        public TjsVarType VarType { get; set; }
        public bool IsParameter { get; set; }
        public string DefaultName => $"{(IsParameter ? "p" : "v")}{Math.Abs(Slot) + 2}";

        public Variable(short slot)
        {
            Slot = slot;
        }

        public Variable(short slot, CodeObject obj)
        {
            Slot = slot;
            IsParameter = CheckIsParameter(obj, slot);
        }

        public static bool CheckIsParameter(CodeObject obj, short slot)
        {
            var argCount = obj.FuncDeclArgCount;
            var varCount = obj.MaxVariableCount;
            if (slot >= -2)
            {
                return false;
            }

            if (slot >= -2 - argCount)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public override string ToString()
        {
            return Name ?? DefaultName;
        }
    }
}
