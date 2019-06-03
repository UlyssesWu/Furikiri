using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    public interface IRegister
    {
        int Size { get; }
        bool Indirect { get; }
    }

    /// <summary>
    /// Ref (%)
    /// </summary>
    class RegisterRef : IRegister
    {
        public short Slot { get; set; }
        public virtual int Size => 1;
        public bool Indirect => true;

        public RegisterRef(short slot)
        {
            Slot = slot;
        }

        public override string ToString()
        {
            return $"%{Slot.ToString()}";
        }
    }

    /// <summary>
    /// Instant (*)
    /// </summary>
    class RegisterValue : IRegister
    {
        public int Size => 1;
        public short Slot { get; set; }
        public bool Indirect => false;
        public object Value { get; set; }
        public RegisterValue(short slot)
        {
            Slot = slot;
        }

        public override string ToString()
        {
            return $"*{Slot.ToString()}";
        }
    }
    class RegisterShort : IRegister
    {
        public int Size => 1;
        public bool Indirect => false;
        public short Value { get; set; }

        public RegisterShort(short value)
        {
            Value = value;
        }

        public override string ToString()
        {
            return Value.ToString();
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

        public RegisterParameter(short slot) : base(slot)
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
