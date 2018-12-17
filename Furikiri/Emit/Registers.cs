using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    public enum FuncParameterExpand : short
    {
        Normal = -3,
        Expand = -2,
        Omit = -1,
        FatNormal = 0,
        FatExpand = 1,
        FatUnnamedExpand = 2,
    }

    class RegisterRef : IRegister
    {
        public int Slot { get; set; }
        public virtual int Size => 1;
        public bool Indirect => true;

        public RegisterRef(int slot)
        {
            Slot = slot;
        }

        public override string ToString()
        {
            return $"%{Slot.ToString()}";
        }
    }

    class RegisterValue : IRegister
    {
        public int Size => 1;
        public int Slot { get; set; }
        public bool Indirect => false;
        public object Value { get; set; }
        public RegisterValue(int slot)
        {
            Slot = slot;
        }

        string IRegister.ToString()
        {
            return $"*{Slot.ToString()}";
        }
    }

    class InstantValue<T> : IRegister
    {
        public int Size => 1;
        public bool Indirect => false;
        public T Value { get; set; }
        public InstantValue(T value)
        {
            Value = value;
        }

        string IRegister.ToString()
        {
            return Value.ToString();
        }
    }

    class RegisterShort : InstantValue<short>
    {
        public RegisterShort(short value) : base(value)
        {
        }
    }

    class RegisterParameter : RegisterRef
    {
        public FuncParameterExpand ParameterExpand { get; set; } = FuncParameterExpand.Normal;

        public override int Size
        {
            get
            {
                if (ParameterExpand == FuncParameterExpand.Normal)
                {
                    return 1;
                }

                return 2;
            }
        }

        public RegisterParameter(int slot) : base(slot)
        {
        }

        public override string ToString()
        {
            if (ParameterExpand == FuncParameterExpand.FatExpand)
            {
                return $"%{Slot.ToString()}*";
            }
            if (ParameterExpand == FuncParameterExpand.FatUnnamedExpand)
            {
                return "*";
            }
            return $"%{Slot.ToString()}";

        }
    }
}
