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
        /// <summary>
        /// incontextof 运算符会先对左侧表达式求值，然后对右侧表达式求值。将左侧表达式的结果作为对象，将这个对象的上下文部分替换为右侧表达式的结果，替换后的对象作为该运算符的结果。
        /// </summary>
        InContextOf,
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