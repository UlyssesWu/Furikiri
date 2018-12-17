using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    class RegisterRef : IRegister
    {
        public int Slot { get; set; }
        public bool Indirect => true;

        public RegisterRef(int slot)
        {
            Slot = slot;
        }
    }

    class RegisterValue : IRegister
    {
        public int Slot { get; set; }
        public bool Indirect => false;
        public object Value { get; set; }
        public RegisterValue(int slot)
        {
            Slot = slot;
        }
    }
}
