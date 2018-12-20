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

    public enum TjsInternalType : short
    {
        Unknown = -1,
        Void = 0,
        Object = 1,
        InterObject = 2,
        /// <summary>
        /// Internal Code Generator
        /// </summary>
        InterGenerator = 10,
        String = 3,
        Octet = 4,
        Real = 5,
        Byte = 6,
        Short = 7,
        Int = 8,
        Long = 9,
    }

    public enum TjsContextType
    {
        TopLevel = 0,
        Function = 1,
        ExprFunction = 2,
        Property = 3,
        PropertySetter = 4,
        PropertyGetter = 5,
        Class = 6,
        SuperClassGetter = 7,
    }
}
