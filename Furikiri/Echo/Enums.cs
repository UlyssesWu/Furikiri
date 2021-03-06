﻿using System;
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
        BitXor,
        LogicAnd,
        LogicOr,
        BitShiftRight,
        /// <summary>
        /// No such op code
        /// </summary>
        BitShiftLeft,
        NumberShiftRight,
        NumberShiftLeft,
        InstanceOf,
        GreaterOrEqual,
        LessOrEqual,
    }

    public enum UnaryOp
    {
        Unknown = -1,
        Inc,
        Dec,
        Not,
        BitNot,
        InvertSign,
        ToInt,
        ToReal,
        ToString,
        ToNumber,
        ToByteArray,
        IsTrue,
        IsFalse,
        TypeOf,
        Invalidate,
        PropertyObject,
    }
}