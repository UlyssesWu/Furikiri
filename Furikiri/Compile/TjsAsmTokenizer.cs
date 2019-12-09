using System;
using System.Collections.Generic;
using System.Text;
using Superpower.Display;

namespace Furikiri.Compile
{
    public enum TjsAsmToken
    {
        ContextType,
        ObjectName,
        ObjectArgCount,
        ConstBegin,
        ConstEnd,
        CodeBegin,
        CodeEnd,
        ConstId,
        VarType,
        StringValue,
        IntValue,
        RealValue,
        OctetValue,
        ObjectValue,
        LabelId,
        Instruction,

        [Token(Example = "=")] Assign,
        [Token(Example = ":")] Label,
        [Token(Example = "(")] LBracket,
        [Token(Example = ")")] RBracket,
        [Token(Example = "//")] Comment,
        [Token(Example = "*")] Const,
        [Token(Example = "%")] Ref,
    }

    class TjsAsmTokenizer
    {
    }
}
