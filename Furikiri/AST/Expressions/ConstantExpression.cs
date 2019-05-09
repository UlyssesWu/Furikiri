using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    /// <summary>
    /// Constant Expression
    /// </summary>
    class ConstantExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.ConstantExpression;
        public override IEnumerable<IAstNode> Children { get; } = null;
        public ITjsVariant Variant { get; set; }
        public TjsVarType DataType => Variant.Type;

        public ConstantExpression(ITjsVariant v)
        {
            Variant = v;
        }

        public override string ToString()
        {
            return Variant.ToString();
        }
    }
}