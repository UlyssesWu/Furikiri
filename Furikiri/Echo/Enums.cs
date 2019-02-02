using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo
{
    public enum BinaryOp
    {
        Unknown = -1,
        Assign,
        Add,
        Sub,
        Mul,
        Div,
        Idiv,
        Equal,
        NotEqual,
        Congruent,
        NotCongruent,
        GreaterThan,
        LessThan,
    }

    public enum UnaryOp
    {
        Unknown = -1,
        Inc,
        Dec,
        Not,
    }
}