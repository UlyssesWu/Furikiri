using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.AST.Expressions
{
    /// <summary>
    /// Invoke Type
    /// </summary>
    public enum InvokeType
    {
        /// <summary>
        /// Normal method call
        /// </summary>
        Call = 0,
        /// <summary>
        /// new()
        /// </summary>
        Ctor,
        /// <summary>
        /// _compile()
        /// </summary>
        RegExpCompile,
    }

    class InvokeExpression : Expression, IInstance
    {
        public override AstNodeType Type => AstNodeType.InvokeExpression;
        public override IEnumerable<IAstNode> Children { get; }

        public string Method
        {
            get => string.IsNullOrEmpty(MethodName) ? MethodExpression.ToString() : MethodName;
            set => MethodName = value;
        }

        public InvokeType InvokeType { get; set; } = InvokeType.Call;

        public string MethodName { get; set; }

        public TjsCodeObject MethodObject { get; set; } //TODO: handle anonymous method

        public Expression MethodExpression { get; set; }

        public Expression Instance { get; set; }

        public List<Expression> Parameters { get; set; } = new List<Expression>();
        public bool HasOmittedArguments { get; set; }

        /// <summary>
        /// 需要展开（*）的参数索引集合
        /// </summary>
        public HashSet<int> SpreadParameterIndices { get; set; }

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

                if (Instance is IdentifierExpression id)
                {
                    // -2 是隐式成员查找代理，本来就不应写出前缀；-1 则是源码
                    // 明确使用的 this，是否隐藏由当前上下文决定。GLOBAL 指令也
                    // 必须保留 global.，否则同名类成员可能截获调用。
                    return id.IdentifierType switch
                    {
                        IdentifierType.ThisProxy => true,
                        IdentifierType.This => id.HideInstance,
                        IdentifierType.Global => false,
                        _ => false
                    };
                }

                return false;
            }
        }

        public override string ToString()
        {
            return DebugString;
        }

        private string DebugString => $"call {Method} ({(string.Join(",", Parameters))})";
    }
}
