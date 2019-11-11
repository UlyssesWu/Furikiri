using System.Collections.Generic;
using System.IO;
using Furikiri.AST.Statements;
using Furikiri.Echo;
using Furikiri.Emit;

namespace Furikiri
{
    internal static class Helper
    {
        private const int NAMESPACE_DEFAULT_HASH_BITS = 3;

        public static string ToRealString(this char[] chars)
        {
            return new string(chars);
        }

        /// <summary>
        /// To short
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public static short ToS(this OpCode code)
        {
            return (short) code;
        }

        public static int RegisterCount(this OpCode code)
        {
            switch (code)
            {
                case OpCode.NOP:
                case OpCode.NF:
                case OpCode.RET:
                case OpCode.EXTRY:
                case OpCode.REGMEMBER:
                case OpCode.DEBUGGER:
                    return 0;

                case OpCode.CL:
                case OpCode.LNOT:
                case OpCode.TT:
                case OpCode.TF:
                case OpCode.SETF:
                case OpCode.SETNF:
                case OpCode.JF:
                case OpCode.JNF:
                case OpCode.JMP:
                case OpCode.INC:
                case OpCode.DEC:
                case OpCode.BNOT:
                case OpCode.TYPEOF:
                case OpCode.EVAL:
                case OpCode.EEXP:
                case OpCode.ASC:
                case OpCode.CHR:
                case OpCode.NUM:
                case OpCode.CHS:
                case OpCode.INV:
                case OpCode.CHKINV:
                case OpCode.INT:
                case OpCode.REAL:
                case OpCode.STR:
                case OpCode.OCTET:
                case OpCode.SRV:
                case OpCode.THROW:
                case OpCode.GLOBAL:
                    return 1;

                case OpCode.CONST:
                case OpCode.CP:
                case OpCode.CCL:
                case OpCode.CEQ:
                case OpCode.CDEQ:
                case OpCode.CLT:
                case OpCode.CGT:
                case OpCode.INCP:
                case OpCode.DECP:
                case OpCode.LOR:
                case OpCode.LAND:
                case OpCode.BOR:
                case OpCode.BXOR:
                case OpCode.BAND:
                case OpCode.SAR:
                case OpCode.SAL:
                case OpCode.SR:
                case OpCode.ADD:
                case OpCode.SUB:
                case OpCode.MOD:
                case OpCode.DIV:
                case OpCode.IDIV:
                case OpCode.MUL:
                case OpCode.CHKINS:
                case OpCode.CALL:
                case OpCode.NEW:
                case OpCode.SETP:
                case OpCode.GETP:
                case OpCode.ENTRY:
                case OpCode.CHGTHIS:
                case OpCode.ADDCI:
                    return 2;

                case OpCode.LORP:
                case OpCode.LANDP:
                case OpCode.BORP:
                case OpCode.BXORP:
                case OpCode.BANDP:
                case OpCode.SARP:
                case OpCode.SALP:
                case OpCode.SRP:
                case OpCode.ADDP:
                case OpCode.SUBP:
                case OpCode.MODP:
                case OpCode.DIVP:
                case OpCode.IDIVP:
                case OpCode.MULP:
                case OpCode.CALLD:
                case OpCode.CALLI:
                case OpCode.GPD:
                case OpCode.SPD:
                case OpCode.SPDE:
                case OpCode.SPDEH:
                case OpCode.GPI:
                case OpCode.SPI:
                case OpCode.SPIE:
                case OpCode.GPDS:
                case OpCode.SPDS:
                case OpCode.GPIS:
                case OpCode.SPIS:
                case OpCode.DELD:
                case OpCode.DELI:
                case OpCode.INCPD:
                case OpCode.INCPI:
                case OpCode.DECPD:
                case OpCode.DECPI:
                case OpCode.TYPEOFD:
                case OpCode.TYPEOFI:
                    return 3;

                case OpCode.LORPD:
                case OpCode.LORPI:
                case OpCode.LANDPD:
                case OpCode.LANDPI:
                case OpCode.BORPD:
                case OpCode.BORPI:
                case OpCode.BXORPD:
                case OpCode.BXORPI:
                case OpCode.BANDPD:
                case OpCode.BANDPI:
                case OpCode.SARPD:
                case OpCode.SARPI:
                case OpCode.SALPD:
                case OpCode.SALPI:
                case OpCode.SRPD:
                case OpCode.SRPI:
                case OpCode.ADDPD:
                case OpCode.ADDPI:
                case OpCode.SUBPD:
                case OpCode.SUBPI:
                case OpCode.MODPD:
                case OpCode.MODPI:
                case OpCode.DIVPD:
                case OpCode.DIVPI:
                case OpCode.IDIVPD:
                case OpCode.IDIVPI:
                case OpCode.MULPD:
                case OpCode.MULPI:
                    return 4;

                case OpCode.LAST:
                case OpCode.PreDec:
                case OpCode.PostInc:
                case OpCode.PostDec:
                case OpCode.Delete:
                case OpCode.FuncCall:
                case OpCode.IgnorePropGet:
                case OpCode.IgnorePropSet:
                default:
                    return 0;
            }
        }

        public static string Read2ByteString(this BinaryReader br)
        {
            var len = br.ReadInt32();
            char[] chars = new char[len];
            for (int i = 0; i < len; i++)
            {
                chars[i] = (char) br.ReadInt16();
            }

            return chars.ToRealString();
        }

        //internal static ITjsVariant ToTjsVariant(this Variant v)
        //{
        //    throw new NotImplementedException();
        //}

        internal static short GetSlot(this IRegister register)
        {
            switch (register)
            {
                case RegisterParameter registerParameter:
                    return registerParameter.Slot;
                case RegisterRef registerRef:
                    return registerRef.Slot;
                case RegisterShort registerShort:
                    return registerShort.Value;
                case RegisterValue registerValue:
                    return registerValue.Slot;
            }

            return 0;
        }

        internal static void SetSlot(this IRegister register, int value)
        {
            SetSlot(register, (short) value);
        }

        internal static void SetSlot(this IRegister register, short value)
        {
            switch (register)
            {
                case RegisterParameter registerParameter:
                    registerParameter.Slot = value;
                    break;
                case RegisterRef registerRef:
                    registerRef.Slot = value;
                    break;
                case RegisterShort registerShort:
                    registerShort.Value = value;
                    break;
                case RegisterValue registerValue:
                    registerValue.Slot = value;
                    break;
            }
        }

        /// <summary>
        /// Convert string to flatten string
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static string Flatten(this string str)
        {
            return str.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        }

        /// <summary>
        /// Convert flatten string to popped string
        /// </summary>
        /// <param name="str"></param>
        /// <returns></returns>
        public static string Pop(this string str)
        {
            return str.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
        }

        /// <summary>
        /// Get name for Context Type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static string ContextTypeName(this TjsContextType type)
        {
            switch (type)
            {
                case TjsContextType.TopLevel:
                    return "top level";
                case TjsContextType.Function:
                    return "function";
                case TjsContextType.ExprFunction:
                    return "function expression";
                case TjsContextType.Property:
                    return "property";
                case TjsContextType.PropertySetter:
                    return "property setter";
                case TjsContextType.PropertyGetter:
                    return "property getter";
                case TjsContextType.Class:
                    return "class";
                case TjsContextType.SuperClassGetter:
                    return "super class getter proxy";
                default:
                    return "unknown";
            }
        }

        internal static int ContextHashSize(this TjsContextType type)
        {
            switch (type)
            {
                case TjsContextType.TopLevel:
                    return 0;
                case TjsContextType.Function:
                    return 1;
                case TjsContextType.ExprFunction:
                    return 1;
                case TjsContextType.Property:
                    return 1;
                case TjsContextType.PropertySetter:
                    return 0;
                case TjsContextType.PropertyGetter:
                    return 0;
                case TjsContextType.Class:
                    return NAMESPACE_DEFAULT_HASH_BITS;
                case TjsContextType.SuperClassGetter:
                    return 0;
                default:
                    return NAMESPACE_DEFAULT_HASH_BITS;
            }
        }

        internal static string GetParamName(this TjsVarType v, int i)
        {
            string s = i < 0 ? "" : i.ToString();
            switch (v)
            {
                case TjsVarType.Null:
                    return $"p{s}";
                case TjsVarType.Void:
                    return $"void{s}";
                case TjsVarType.Object:
                    return $"o{s}";
                case TjsVarType.String:
                    return $"s{s}";
                case TjsVarType.Octet:
                    return $"bytes{s}";
                case TjsVarType.Int:
                    return $"i{s}";
                case TjsVarType.Real:
                    return $"d{s}";
                default:
                    return $"p{s}";
            }
        }

        internal static string ToSymbol(this BinaryOp op)
        {
            switch (op)
            {
                case BinaryOp.Assign:
                    return "=";
                case BinaryOp.Add:
                    return "+";
                case BinaryOp.Sub:
                    return "-";
                case BinaryOp.Mul:
                    return "*";
                case BinaryOp.Div:
                    return "/";
                case BinaryOp.Idiv:
                    return "\\";
                case BinaryOp.Equal:
                    return "==";
                case BinaryOp.NotEqual:
                    return "!=";
                case BinaryOp.Congruent:
                    return "===";
                case BinaryOp.NotCongruent:
                    return "!==";
                case BinaryOp.LessThan:
                    return "<";
                case BinaryOp.GreaterThan:
                    return ">";
                case BinaryOp.InstanceOf:
                    return "instanceof";
                case BinaryOp.BitXor:
                    return "^";
                case BinaryOp.BitAnd:
                    return "&";
                case BinaryOp.BitOr:
                    return "|";
                case BinaryOp.NumberShiftLeft:
                    return "<<";
                case BinaryOp.NumberShiftRight:
                    return ">>";
                case BinaryOp.BitShiftRight:
                    return ">>>";
                case BinaryOp.BitShiftLeft:
                    return "<<<";
                case BinaryOp.LogicAnd:
                    return "&&";
                case BinaryOp.LogicOr:
                    return "||";
                default:
                    return "#";
            }
        }

        internal static int GetPrecedence(this UnaryOp op)
        {
            switch (op)
            {
                case UnaryOp.TypeOf:
                case UnaryOp.Invalidate:
                case UnaryOp.ToByteArray:
                case UnaryOp.ToInt:
                case UnaryOp.ToNumber:
                case UnaryOp.ToReal:
                case UnaryOp.ToString:
                case UnaryOp.PropertyObject:
                    return 1;
                default:
                    return 2;
            }
        }

        internal static int GetPrecedence(this BinaryOp op)
        {
            //TODO: haven't find doc about this, currently using other language's impl
            switch (op)
            {
                case BinaryOp.InstanceOf:
                    return 1;
                //2 is unary
                case BinaryOp.Mul:
                case BinaryOp.Div:
                    return 3;
                case BinaryOp.Add:
                case BinaryOp.Sub:
                case BinaryOp.Idiv:
                    return 4;
                case BinaryOp.NumberShiftLeft:
                case BinaryOp.NumberShiftRight:
                case BinaryOp.BitShiftRight:
                case BinaryOp.BitShiftLeft:
                    return 5;
                case BinaryOp.LessThan:
                case BinaryOp.GreaterThan:
                case BinaryOp.GreaterOrEqual:
                case BinaryOp.LessOrEqual:
                    return 6;
                case BinaryOp.Equal:
                case BinaryOp.NotEqual:
                case BinaryOp.Congruent:
                case BinaryOp.NotCongruent:
                    return 7;
                case BinaryOp.BitAnd:
                    return 8;
                case BinaryOp.BitXor:
                    return 9;
                case BinaryOp.BitOr:
                    return 10;
                case BinaryOp.LogicAnd:
                    return 11;
                case BinaryOp.LogicOr:
                    return 12;
                //13 is ?:
                case BinaryOp.Assign:
                    return 14;
                default:
                    return 0;
            }
        }

        internal static string ToSymbol(this UnaryOp op)
        {
            switch (op)
            {
                case UnaryOp.Inc:
                    return "++";
                case UnaryOp.Dec:
                    return "--";
                case UnaryOp.Not:
                    return "!";
                case UnaryOp.BitNot:
                    return "~";
                case UnaryOp.InvertSign:
                    return "-";
                case UnaryOp.ToInt:
                    return "(int)";
                case UnaryOp.ToReal:
                    return "(real)";
                case UnaryOp.ToString:
                    return "(string)";
                case UnaryOp.ToNumber:
                    return "(number)"; //FIXME: fix this
                case UnaryOp.ToByteArray:
                    return "(octet)"; //FIXME: fix this
                case UnaryOp.IsTrue:
                    return "";
                case UnaryOp.IsFalse:
                    return "!";
                case UnaryOp.Invalidate:
                    return "invalidate";
                case UnaryOp.TypeOf:
                    return "typeof";
                default:
                    break;
            }

            return "";
        }

        /// <summary>
        /// Is OpCode a Jump
        /// </summary>
        /// <param name="code"></param>
        /// <param name="conditionalOnly">if true, only JF & JNF counts</param>
        /// <returns></returns>
        public static bool IsJump(this OpCode code, bool conditionalOnly = false)
        {
            if (conditionalOnly)
            {
                return code == OpCode.JF || code == OpCode.JNF;
            }

            return code == OpCode.JMP || code == OpCode.JF || code == OpCode.JNF;
        }

        public static bool IsCompare(this BinaryOp code)
        {
            switch (code)
            {
                case BinaryOp.NotEqual:
                case BinaryOp.Equal:
                case BinaryOp.NotCongruent:
                case BinaryOp.Congruent:
                case BinaryOp.GreaterThan:
                case BinaryOp.LessThan:
                    return true;
            }

            return false;
        }

        public static bool IsCompare(this UnaryOp code)
        {
            switch (code)
            {
                case UnaryOp.Not:
                    return true;
            }

            return false;
        }

        public static string AsString(this IRegisterData data)
        {
            if (data is OperandData op)
            {
                return op.Variant.Value.ToString();
            }

            return null;
        }

        public static bool TryAdd<T>(this List<T> list, T obj)
        {
            if (list.Contains(obj))
            {
                return false;
            }

            list.Add(obj);
            return true;
        }

        public static void AddRange<T>(this HashSet<T> hs, IEnumerable<T> set)
        {
            hs.UnionWith(set);
            //foreach (var t in set)
            //{
            //    hs.Add(t);
            //}
        }

        public static T TryGet<T>(this List<T> list, int index)
        {
            if (list.Count > index)
            {
                return list[index];
            }

            return default;
        }

        public static V TryGet<K, V>(this Dictionary<K, V> dic, K key)
        {
            if (key != null && dic.ContainsKey(key))
            {
                return dic[key];
            }

            return default;
        }

        public static void Replace<T>(this List<T> l, T from, T to)
        {
            l[l.IndexOf(from)] = to;
        }

        internal static void SafeHide(this List<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (block.Statements.Count == 1 && block.Statements[0] is Statement)
                {
                    continue;
                }

                block.Hidden = true;
            }
        }
    }
}