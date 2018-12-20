namespace Furikiri.Emit
{
    public interface ITjsVariant
    {
        TjsVarType Type { get; }
        object Value { get; }
    }

    public class TjsVoid : ITjsVariant
    {
        private static TjsVoid _void;
        public TjsVarType Type => TjsVarType.Void;
        public object Value => null;

        private TjsVoid() { }

        /// <summary>
        /// Void
        /// </summary>
        public static TjsVoid Void => _void ?? (_void = new TjsVoid());
    }

    public class TjsObject : ITjsVariant
    {
        public TjsVarType Type => TjsVarType.Object;
        public object Value { get; set; }
        public bool Internal { get; set; } = false;

        public TjsObject(object obj)
        {
            Value = obj;
        }
    }

    public class TjsString : ITjsVariant
    {
        public TjsVarType Type => TjsVarType.String;
        public object Value => StringValue;
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
    }

    public class TjsOctet : ITjsVariant
    {
        public TjsVarType Type => TjsVarType.Octet;
        public object Value => BytesValue;
        public byte[] BytesValue { get; set; }

        public TjsOctet(byte[] bytes)
        {
            BytesValue = bytes;
        }
    }

    public class TjsInt : ITjsVariant
    {
        internal TjsInternalType InternalType { get; set; }

        public TjsVarType Type => TjsVarType.Int;
        public object Value => IntValue;
        public int IntValue { get; set; }

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
            IntValue = val;
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
            return i.IntValue;
        }

        public static explicit operator TjsInt(int i)
        {
            return new TjsInt(i);
        }
    }

    public class TjsReal : ITjsVariant
    {
        internal TjsInternalType InternalType { get; set; }

        public TjsVarType Type => TjsVarType.Real;
        public object Value => InternalType == TjsInternalType.Long ? LongValue : DoubleValue;
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
            return d.DoubleValue;
        }

        public static explicit operator TjsReal(double d)
        {
            return new TjsReal(d);
        }
    }
}
