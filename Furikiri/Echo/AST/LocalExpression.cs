using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.Echo.Patterns;
using Furikiri.Emit;

namespace Furikiri.Echo.AST
{
    /// <summary>
    /// Define of a var
    /// </summary>
    class LocalExpression : Expression
    {
        public bool IsParameter { get; private set; } = false;
        public TjsVarType VarType { get; set; }
        public override AstNodeType Type => AstNodeType.LocalExpression;
        public override List<IAstNode> Children { get; } = null;
        public short Slot { get; set; }
        public string Name { get; set; }
        public string DefaultName => Name ?? $"{(IsParameter ? "p" : "v")}{Math.Abs(Slot) + 2}";

        public LocalExpression(bool isParam, short slot)
        {
            IsParameter = isParam;
            Slot = slot;
        }

        public override string ToString()
        {
            if (IsParameter)
            {
                return DefaultName;
            }

            return $"var {DefaultName}";
        }
    }
}