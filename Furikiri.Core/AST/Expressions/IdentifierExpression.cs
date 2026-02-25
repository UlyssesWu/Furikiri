using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    public enum IdentifierType
    {
        Normal = 1,

        /// <summary>
        /// This = -1
        /// </summary>
        This = -1,

        /// <summary>
        /// This Proxy = -2
        /// </summary>
        ThisProxy = -2,

        /// <summary>
        /// Global
        /// </summary>
        Global = 0,
    }

    class IdentifierExpression : Expression, IInstance
    {
        public override AstNodeType Type => AstNodeType.IdentifierExpression;
        public override IEnumerable<IAstNode> Children { get; }
        
        public string Name { get; set; }

        public IdentifierType IdentifierType { get; set; }

        public IdentifierExpression(string name, IdentifierType idType = IdentifierType.Normal)
        {
            Name = name;
            IdentifierType = idType;
        }

        public string FullName
        {
            get
            {
                if (Instance != null)
                {
                    if (Instance is IdentifierExpression id)
                    {
                        if (id.IdentifierType != IdentifierType.Normal && id.HideInstance)
                        {
                            return Name;
                        }

                        if (string.IsNullOrEmpty(id.Name))
                        {
                            return Name;
                        }

                        return $"{id.FullName}.{Name}";
                    }

                    if (Instance is LocalExpression local)
                    {
                        return $"{local}.{Name}";
                    }
                }

                return Name;
            }
        }

        public override string ToString()
        {
            return Name;
        }

        public bool HideInstance { get; set; }
        public Expression Instance { get; set; }
    }
}