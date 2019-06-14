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
        public Variable VariableDef { get; set; }
        public TjsVarType DataType { get; set; }
        public override AstNodeType Type => AstNodeType.LocalExpression;
        public override IEnumerable<IAstNode> Children { get; } = null;
        public short Slot => VariableDef.Slot;
        public string Name => VariableDef.DefaultName;
        public bool IsParameter => VariableDef.IsParameter;
        
        public LocalExpression(Variable v)
        {
            VariableDef = v;
        }

        public LocalExpression(CodeObject obj, short slot)
        {
            VariableDef = new Variable(slot, obj);
        }
        
        public override string ToString()
        {
            return VariableDef.ToString();
        }
    }
}