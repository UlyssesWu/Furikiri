using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    public static class Const
    {
        /// <summary>
        /// %0, res, usually void
        /// </summary>
        public const short Resource = 0;
        /// <summary>
        /// %-1, this
        /// </summary>
        public const short This = -1;
        /// <summary>
        /// %-2, this ?? global
        /// </summary>
        public const short ThisProxy = -2;
    }

    [Flags]
    public enum TjsInterfaceFlag : int
    {
        /// <summary>
        /// create a member if not exists
        /// </summary>
        MemberEnsure = 0x00000200,
        /// <summary>
        /// member *must* exist (for Dictionary/Array)
        /// </summary>
        MemberMustExist = 0x00000400,
        /// <summary>
        /// ignore property invoking
        /// </summary>
        IgnorePropInvoking = 0x00000800,
        /// <summary>
        /// member is hidden
        /// </summary>
        HiddenMember = 0x00001000,
        /// <summary>
        /// member is not registered to the object (internal use)
        /// </summary>
        StaticMember = 0x00010000,
        /// <summary>
        /// values are not retrieved (for EnumMembers)
        /// </summary>
        EnumNoValue = 0x00100000,

        NisRegister = 0x00000001,

        NisGetInstance = 0x00000002,

        CiiAdd = 0x00000001,

        CiiGet = 0x00000000,

        CiiSetFinalize = 0x00000002,

        CiiSetMissing = 0x00000003,
    }
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
        Unknown = -2,
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
