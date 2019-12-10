using Furikiri.Emit;
using Superpower;
using Superpower.Display;
using Superpower.Model;
using Superpower.Parsers;
using Superpower.Tokenizers;

// ReSharper disable InconsistentNaming

namespace Furikiri.Compile
{
    public enum TjsAsmToken
    {
        Text,
        TypeDescription,
        Register,
        Const,
        Hex,
        [Token(Example = "ArgCount=0")] Property,

        StringValue,
        IntValue,
        RealValue,
        OctetValue,
        //ObjectValue,

        ConstBegin,
        ConstEnd,
        CodeBegin,
        CodeEnd,

        [Token(Example = "=")] Assign,
        [Token(Example = ":")] Colon,
        [Token(Example = "(")] LBracket,
        [Token(Example = ")")] RBracket,
        [Token(Example = "//")] Comment,
        [Token(Example = "*")] Star,
        [Token(Example = "%")] Percent,
        [Token(Example = ".")] Dot,
        [Token(Example = "[")] LSBracket,
        [Token(Example = "]")] RSBracket,
    }

    public class TjsAsmTokenizer
    {
        static TextParser<Unit> TypeDescriptionToken =>
            from begin in Character.EqualTo('(')
            from content in Character.Except(')').IgnoreMany()
            from end in Character.EqualTo(')')
            select Unit.Value;

        static TextParser<Unit> RegisterToken =>
            from begin in Character.EqualTo('%')
            from content in Numerics.Integer
            select Unit.Value;

        static TextParser<Unit> ConstToken =>
            from begin in Character.EqualTo('*')
            from content in Numerics.Integer
            select Unit.Value;

        static TextParser<Unit> OctetToken =>
            from begin in Span.EqualTo("<%")
            from content in Numerics.HexDigits.IgnoreMany()
            from end in Span.EqualTo("%>")
            select Unit.Value;

        static TextParser<Unit> HexToken =>
            from begin in Span.EqualToIgnoreCase("0x")
            from content in Numerics.HexDigits
            select Unit.Value;

        static TextParser<Unit> PropertyToken =>
            from start in Character.EqualTo('[')
            from name in Character.Except('=').IgnoreMany()
            from equal in Character.EqualTo('=')
            from val in Character.Except(']').IgnoreMany()
            from end in Character.EqualTo(']')
            select Unit.Value;

        public static Tokenizer<TjsAsmToken> Instance =>
            new TokenizerBuilder<TjsAsmToken>()
                .Ignore(Span.WhiteSpace)
                .Match(Span.EqualTo(Assembler.ConstSectionBegin), TjsAsmToken.ConstBegin, true)
                .Match(Span.EqualTo(Assembler.ConstSectionEnd), TjsAsmToken.ConstEnd, true)
                .Match(Span.EqualTo(Assembler.CodeSectionBegin), TjsAsmToken.CodeBegin, true)
                .Match(Span.EqualTo(Assembler.CodeSectionEnd), TjsAsmToken.CodeEnd, true)
                .Match(TypeDescriptionToken, TjsAsmToken.TypeDescription, false)
                .Match(PropertyToken, TjsAsmToken.Property, false)
                .Match(RegisterToken, TjsAsmToken.Register, true)
                .Match(ConstToken, TjsAsmToken.Const, true)
                .Match(QuotedString.CStyle, TjsAsmToken.StringValue, false)
                .Match(HexToken, TjsAsmToken.Hex, true)
                .Match(Numerics.Integer, TjsAsmToken.IntValue, true)
                .Match(Numerics.Decimal, TjsAsmToken.RealValue, true)
                .Match(OctetToken, TjsAsmToken.OctetValue, false)
                .Match(Span.EqualTo("="), TjsAsmToken.Assign, false)
                .Match(Span.EqualTo(":"), TjsAsmToken.Colon, false)
                .Match(Span.EqualTo("("), TjsAsmToken.LBracket, false)
                .Match(Span.EqualTo(")"), TjsAsmToken.RBracket, false)
                .Match(Span.EqualTo("//"), TjsAsmToken.Comment, false)
                .Match(Span.EqualTo("*"), TjsAsmToken.Star, false)
                .Match(Span.EqualTo("%"), TjsAsmToken.Percent, false)
                .Match(Span.EqualTo("."), TjsAsmToken.Dot, false)
                .Match(Span.EqualTo("["), TjsAsmToken.LSBracket, false)
                .Match(Span.EqualTo("]"), TjsAsmToken.RSBracket, false)
                .Match(Span.NonWhiteSpace, TjsAsmToken.Text, true)
                .Build(); //keep last
    }
}