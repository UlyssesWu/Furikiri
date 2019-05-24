using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    class PropertyAccessExpression : Expression, IInstance
    {
        public override AstNodeType Type => AstNodeType.PropertyAccessExpression;
        public override IEnumerable<IAstNode> Children { get; }

        public Expression Property { get; set; }

        public PropertyAccessExpression(Expression property, Expression instance)
        {
            Property = property;
            Instance = instance;
        }

        public bool HideInstance { get; }
        public Expression Instance { get; set; }
    }
}
