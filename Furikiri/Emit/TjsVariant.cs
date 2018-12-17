namespace Furikiri.Emit
{
    public enum TjsVarType
    {
        Null = -1,
        Void = 0,
        Object = 1,
        String = 2,
        Octet = 3,
        Int = 4,
        Real = 5,
    }

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

        public static TjsVoid Void => _void ?? (_void = new TjsVoid());
    }

    public class TjsObject : ITjsVariant
    {
        public TjsVarType Type => TjsVarType.Object;
        public object Value { get; set; }

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
        public TjsVarType Type => TjsVarType.Int;
        public object Value => IntValue;
        public int IntValue { get; set; }

        public TjsInt(int val)
        {
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
        public TjsVarType Type => TjsVarType.Real;
        public object Value => DoubleValue;
        public double DoubleValue { get; set; }

        public TjsReal(double val)
        {
            DoubleValue = val;
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
