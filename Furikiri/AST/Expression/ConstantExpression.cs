using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.AST.Expression
{
    /// <summary>
    /// Constant Expression
    /// </summary>
    class ConstantExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.ConstantExpression;
        public override List<IAstNode> Children { get; } = null;
        public ITjsVariant Variant { get; set; }
        public TjsVarType VarType => Variant.Type;

        public ConstantExpression(ITjsVariant v)
        {
            Variant = v;
        }
    }
}