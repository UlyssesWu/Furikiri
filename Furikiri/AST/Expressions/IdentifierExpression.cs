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

    class IdentifierExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.IdentifierExpression;
        public override IEnumerable<IAstNode> Children { get; }

        public Expression Child { get; set; }

        public string Name { get; set; }

        public IdentifierType IdentifierType { get; set; } = IdentifierType.Normal;

        /// <summary>
        /// (this.)name
        /// </summary>
        public bool Implicit { get; set; }

        public IdentifierExpression(string name, IdentifierType idType = IdentifierType.Normal)
        {
            Name = name;
            IdentifierType = idType;
        }

        public string FullName
        {
            get
            {
                if (Parent != null)
                {
                    if (Parent is IdentifierExpression id)
                    {
                        if (id.IdentifierType != IdentifierType.Normal && id.Implicit)
                        {
                            return Name;
                        }

                        if (string.IsNullOrEmpty(id.Name))
                        {
                            return Name;
                        }

                        return $"{id.FullName}.{Name}";
                    }
                }

                return Name;
            }
        }

        public override string ToString()
        {
            return Name;
        }
    }
}