using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    class InvokeExpression : Expression, IInstance
    {
        public override AstNodeType Type => AstNodeType.InvokeExpression;
        public override IEnumerable<IAstNode> Children { get; }

        public string Method
        {
            get => string.IsNullOrEmpty(MethodName) ? MethodExpression.ToString() : MethodName;
            set => MethodName = value;
        }

        public bool IsCtor { get; set; }

        public string MethodName { get; set; }

        public TjsCodeObject MethodObject { get; set; } //TODO: handle anonymous method

        public Expression MethodExpression { get; set; }

        public Expression Instance { get; set; }

        public List<Expression> Parameters { get; set; } = new List<Expression>();

        public InvokeExpression(string name)
        {
            MethodName = name;
        }

        public InvokeExpression(TjsCodeObject methodObj)
        {
            MethodObject = methodObj;
        }

        public InvokeExpression(Expression exp)
        {
            MethodExpression = exp;
            MethodExpression.Parent = this;
        }

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
    }
}