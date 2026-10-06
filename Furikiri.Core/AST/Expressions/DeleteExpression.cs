using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    class DeleteExpression : Expression, IInstance
    {
        public override AstNodeType Type => AstNodeType.DeleteExpression;
        public override IEnumerable<IAstNode> Children
        {
            get
            {
                if (Instance != null) yield return Instance;
                if (IdentifierExpression != null) yield return IdentifierExpression;
            }
        }

        public bool HideInstance
        {
            get
            {
                if (Instance == null)
                {
                    return true;
                }

                if (Instance is IdentifierExpression id && id.IdentifierType == IdentifierType.ThisProxy)
                {
                    return true;
                }

                return false;
            }
        }

        public Expression Instance { get; set; }
        public string IdentifierName { get; set; }
        public Expression IdentifierExpression { get; set; }
        public string Identifier
        {
            get => string.IsNullOrEmpty(IdentifierName) ? IdentifierExpression.ToString() : IdentifierName;
            set => IdentifierName = value;
        }

        public DeleteExpression(string name)
        {
            IdentifierName = name;
        }

        public DeleteExpression(Expression name)
        {
            IdentifierExpression = name;
            IdentifierExpression.Parent = this;
        }
    }
}
