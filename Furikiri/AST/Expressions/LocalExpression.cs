using System;
using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    /// <summary>
    /// Define of a var
    /// </summary>
    class LocalExpression : Expression
    {
        public bool IsParameter { get; private set; } = false;
        public TjsVarType VarType { get; set; }
        public override AstNodeType Type => AstNodeType.LocalExpression;
        public override IEnumerable<IAstNode> Children { get; } = null;
        public short Slot { get; set; }
        public string Name { get; set; }
        public string DefaultName => Name ?? $"{(IsParameter ? "p" : "v")}{Math.Abs(Slot) + 2}";

        public LocalExpression(bool isParam, short slot)
        {
            IsParameter = isParam;
            Slot = slot;
        }

        public LocalExpression(CodeObject obj, short slot)
        {
            IsParameter = CheckIsParameter(obj, slot);
            Slot = slot;
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
            return DefaultName;
        }
    }
}