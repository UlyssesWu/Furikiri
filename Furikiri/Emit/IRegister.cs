using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    interface IRegister
    {
        int Size { get; }
        bool Indirect { get; }
        string ToString();
    }
}
