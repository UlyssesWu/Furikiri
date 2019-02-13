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
        Mod,
        Equal,
        NotEqual,
        Congruent,
        NotCongruent,
        GreaterThan,
        LessThan,
        BitAnd,
        BitOr,
        LogicAnd,
        LogicOr,
        BitShiftRight,
        BitShiftLeft,
        NumberShiftRight,
        NumberShiftLeft,
        InstanceOf,
    }

    public enum UnaryOp
    {
        Unknown = -1,
        Inc,
        Dec,
        Not,
        InvertSign,
        ToInt,
        ToReal,
        ToString,
        ToNumber,
        ToByteArray,
        IsTrue,
        IsFalse,

    }
}