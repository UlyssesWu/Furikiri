using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.AST.Expressions
{
    class DeleteExpression : Expression, IInstance
    {
        public override AstNodeType Type => AstNodeType.DeleteExpression;
        public override IEnumerable<IAstNode> Children { get; }

        public bool HideInstance
        {
            get
            {
                if (Instance == null)
                {
                    return true;
                }

                if (Instance is IdentifierExpression id && id.IdentifierType != IdentifierType.Normal)
                {
                    return true;
                }

                return false;
            }
        }

        public Expression Instance { get; }
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
        }
    }
}
