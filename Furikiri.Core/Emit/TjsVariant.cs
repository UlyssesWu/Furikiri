using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace Furikiri.Emit
{
    public interface ITjsVariant
    {
        TjsVarType Type { get; }
        object Value { get; }

        string DebugString { get; }
    }

    // null 是表示“哪个对象都不表示”的对象。与 void 不同。
    // 对 null 对象进行操作会导致错误。
    // 使用 incontextof 运算符将函数的上下文改成 null 后，那个函数会在被调用位置的上下文中执行。

    /// <summary>
    /// void 表示“什么也没有”。应用于各种表现为“什么也没有”的场合。与 null 不同。
    /// 已声明了的变量 (什么东西都没有代入的变量) 的值就是 void 。
    /// 作为字符串来处理的时候相当于空 字符串 ( '' )。作为数值来处理的时候相当于 0 。
    /// </summary>
    [DebuggerDisplay("{DebugString}")]
    public class TjsVoid : ITjsVariant
    {
        private static TjsVoid _void;
        public TjsVarType Type => TjsVarType.Void;
        public object Value => null;

        private TjsVoid()
        {
        }

        /// <summary>
        /// Void
        /// </summary>
        public static TjsVoid Void => _void ??= new TjsVoid();

        public string DebugString => "(void)";

        public override string ToString()
        {
            return "void";
        }
    }

    //[DebuggerDisplay("DebugString")]
    public class TjsObject : ITjsVariant
    {
        public TjsVarType Type => TjsVarType.Object;
        public object Value { get; set; }
        public string DebugString => Value?.ToString() ?? "(void)";
        public bool Internal { get; set; } = false;

        public TjsObject(object obj)
        {
            Value = obj;
        }

        public override string ToString()
        {
            // 字节码中的 Object(null) 是 TJS2 的 null 常量，不是 CLR 类型名。
            return Value?.ToString() ?? "null";
        }
    }

    [DebuggerDisplay("{DebugString}")]
    public class TjsCodeObject : ITjsVariant
    {
        public TjsVarType Type => TjsVarType.Object;
        public object Value => Object;

        public string DebugString
        {
            get
            {
                string objName = Object?.Name;
                objName = objName == null ? "0x00000000" : $"[{objName}]";
                if (Object?.ContextType == TjsContextType.ExprFunction)
                {
                    objName += $"(0x{Object.GetHashCode():X8})";
                }

                string thisName = This?.Name;
                thisName = thisName == null ? "0x00000000" : $"[{objName}]";
                return $"(object)({objName}:{thisName})";
            }
        }

        public CodeObject Object { get; set; }
        public CodeObject This { get; set; } = null;
        public bool HasThis => This != null;
        public bool Internal { get; set; } = true;

        public TjsCodeObject(CodeObject obj)
        {
            Object = obj;
        }

        public TjsCodeObject(CodeObject obj, CodeObject ths)
        {
            Object = obj;
            This = ths;
        }

        public override string ToString()
        {
            if (Object.IsLambda)
            {
                return $"({Object.ContextType.ContextTypeName()})0x{Object.GetHashCode():X8}";
            }
            return $"({Object.ContextType.ContextTypeName()}){Object.Name}";
        }
    }

    [DebuggerDisplay("{DebugString}")]
    public class TjsString : ITjsVariant
    {
        public TjsVarType Type => TjsVarType.String;
        public object Value => StringValue;
        public string DebugString => $"(string)\"{StringValue}\"";

        public string StringValue { get; set; }

        public TjsString(string str)
        {
            StringValue = str;
        }

        public static implicit operator string(TjsString str)
        {
            return str.StringValue;
        }

        public static explicit operator TjsString(string str)
        {
            return new TjsString(str);
        }

        public override string ToString()
        {
            var escaped = (StringValue ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
            return $"\"{escaped}\"";
        }
    }

    [DebuggerDisplay("{DebugString}")]
    public class TjsOctet : ITjsVariant
    {
        public TjsVarType Type => TjsVarType.Octet;
        public object Value => BytesValue;
        public string DebugString => $"(octet)<% {BitConverter.ToString(BytesValue)} %>";
        public byte[] BytesValue { get; set; }

        public TjsOctet(byte[] bytes)
        {
            BytesValue = bytes;
        }

        public override string ToString()
        {
            var str = string.Join(" ", BytesValue.Select(b => b.ToString("X2")));
            return $"<% {str} %>";
        }
    }

    [DebuggerDisplay("{DebugString}")]
    public class TjsInt : ITjsVariant
    {
        internal TjsInternalType InternalType { get; set; }

        public TjsVarType Type => TjsVarType.Int;
        public object Value => IntValue;
        public string DebugString => $"({InternalType.ToString().ToLowerInvariant()}){IntValue}";
        // DATA 中的 byte/short/int/long 只是存储压缩形式，运行时都是 64 位整数。
        public long IntValue { get; set; }

        public TjsInt(long val)
        {
            InternalType = TjsInternalType.Long;
            IntValue = val;
        }

        /// <summary>
        /// Int
        /// </summary>
        /// <param name="val"></param>
        public TjsInt(int val)
        {
            InternalType = TjsInternalType.Int;
            IntValue = val;
        }

        /// <summary>
        /// Byte
        /// </summary>
        /// <param name="val"></param>
        public TjsInt(byte val)
        {
            InternalType = TjsInternalType.Byte;
            IntValue = (sbyte)val;
        }

        /// <summary>
        /// Short
        /// </summary>
        /// <param name="val"></param>
        public TjsInt(short val)
        {
            InternalType = TjsInternalType.Short;
            IntValue = val;
        }

        public static implicit operator int(TjsInt i)
        {
            return checked((int)i.IntValue);
        }

        public static explicit operator TjsInt(int i)
        {
            return new TjsInt(i);
        }

        public override string ToString()
        {
            return IntValue.ToString(CultureInfo.InvariantCulture);
        }
    }

    [DebuggerDisplay("{DebugString}")]
    public class TjsReal : ITjsVariant
    {
        internal TjsInternalType InternalType { get; set; }

        public TjsVarType Type => InternalType == TjsInternalType.Long ? TjsVarType.Int : TjsVarType.Real;
        // 兼容旧 long 构造入口时必须先装箱，条件表达式的数值提升会舍入超过 2^53 的整数。
        public object Value => InternalType == TjsInternalType.Long ? (object)LongValue : DoubleValue;
        public string DebugString => $"(real){Value}";
        public double DoubleValue { get; set; }
        public long LongValue { get; set; }

        /// <summary>
        /// Double
        /// </summary>
        /// <param name="val"></param>
        public TjsReal(double val)
        {
            InternalType = TjsInternalType.Real;
            DoubleValue = val;
        }

        /// <summary>
        /// Long
        /// </summary>
        /// <param name="val"></param>
        public TjsReal(long val)
        {
            InternalType = TjsInternalType.Long;
            LongValue = val;
        }

        public static implicit operator double(TjsReal d)
        {
            return d.InternalType == TjsInternalType.Long ? d.LongValue : d.DoubleValue;
        }

        public static explicit operator TjsReal(double d)
        {
            return new TjsReal(d);
        }

        public override string ToString()
        {
            if (InternalType == TjsInternalType.Long)
            {
                return LongValue.ToString(CultureInfo.InvariantCulture);
            }

            // TJS2 的源码关键字是 NaN / Infinity；运行时区域设置提供的
            // “∞”等显示符号虽然有时能被词法器接受，却不是规范化源码，且
            // 在不同系统语言下不稳定。有限实数也统一使用点号作为小数点。
            if (double.IsNaN(DoubleValue))
            {
                return "NaN";
            }
            if (double.IsPositiveInfinity(DoubleValue))
            {
                return "Infinity";
            }
            if (double.IsNegativeInfinity(DoubleValue))
            {
                return "-Infinity";
            }
            var literal = DoubleValue.ToString("R", CultureInfo.InvariantCulture);
            // 1 和 1.0 的 typeof 不同；-0 也会被词法器读成无符号的整数零。
            // 保留小数点才能保持实数类型、负零及后续除法的符号。
            return literal.IndexOf('.') < 0 && literal.IndexOf('E') < 0 && literal.IndexOf('e') < 0
                ? literal + ".0"
                : literal;
        }
    }

    [DebuggerDisplay("{DebugString}")]
    internal class TjsStub : ITjsVariant
    {
        public short Slot { get; set; }
        public TjsVarType Type { get; set; }
        public object Value => TjsValue;
        public ITjsVariant TjsValue { get; set; }
        public string DebugString => "(stub)" + TjsValue.DebugString;

        public TjsStub(short slot, TjsVarType type)
        {
            Slot = slot;
            Type = type;
        }

        public TjsStub(TjsVarType type = TjsVarType.Unknown)
        {
            Type = type;
        }
    }
}
