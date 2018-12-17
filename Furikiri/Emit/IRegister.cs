using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    interface IRegister
    {
        bool Indirect { get; }
    }
}
