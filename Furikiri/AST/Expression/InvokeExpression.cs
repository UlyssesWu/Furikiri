﻿using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.AST.Expression
{
    class InvokeExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.InvokeExpression;
        public override List<IAstNode> Children { get; } = new List<IAstNode>();

        public string Method { get; set; }

        public TjsCodeObject MethodObject { get; set; } //TODO: handle anonymous method

        public Expression MethodExpression { get; set; }

        public Expression Caller { get; set; }

        //public List<Expression> Parameters { get; set; } = new List<Expression>();

        public InvokeExpression(string name)
        {
            Method = name;
        }

        public InvokeExpression(TjsCodeObject methodObj)
        {
            MethodObject = methodObj;
        }

        public InvokeExpression(Expression exp)
        {
            MethodExpression = exp;
        }
    }
}