using System;
using System.Collections.Generic;
using System.Linq;
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
        [Token(Example = "[ArgCount=0]")] Property,
        SingleLineComment,
        Label,
        OpCode,
        OpParameter,

        [Token(Category = "Value")] StringValue,
        [Token(Category = "Value")] IntValue,
        [Token(Category = "Value")] RealValue,
        [Token(Category = "Value")] OctetValue,
        //ObjectValue,

        ConstBegin,
        ConstEnd,
        CodeBegin,
        CodeEnd,

        [Token(Example = "=")] Assign,
        [Token(Example = ":")] Colon,
        [Token(Example = "(")] LBracket,
        [Token(Example = ")")] RBracket,
        [Token(Example = "//")] CommentSign,
        [Token(Example = "*")] Star,
        [Token(Example = "%")] Percent,
        [Token(Example = ".")] Dot,
        [Token(Example = ",")] Comma,
        [Token(Example = "[")] LSBracket,
        [Token(Example = "]")] RSBracket,
        [Token(Example = "<%")] LOctetBracket,
        [Token(Example = "%>")] ROctetBracket,
    }

    public static class TjsAsmTokenizer
    {
        internal static readonly TextParser<string> TypeDescriptionToken =
            from begin in Character.EqualTo('(')
            from content in Character.Except(')').Many()
            from end in Character.EqualTo(')')
            select new string(content);

        internal static readonly TextParser<int> RegisterToken =
            from begin in Character.EqualTo('%')
            from content in Numerics.IntegerInt32
            select content;

        internal static readonly TextParser<int> ConstToken =
            from begin in Character.EqualTo('*')
            from content in Numerics.IntegerInt32
            select content;

        internal static readonly TextParser<byte[]> OctetToken =
            from begin in Span.EqualTo("<%")
            from content in Numerics.HexDigits.Many()
            from end in Span.EqualTo("%>")
            select OctetSpanToBytes(content);

        internal static readonly TextParser<OpCode> OpCodeToken =
            from code in OpCodeParser()
            select code;

        private static byte[] OctetSpanToBytes(TextSpan[] spans)
        {
            List<byte> bytes = new List<byte>();
            foreach (var span in spans)
            {
                bytes.AddRange(span.ToStringValue().HexStringToBytes());
            }

            return bytes.ToArray();
        }

        private static TextParser<OpCode> OpCodeParser()
        {
            TextParser<OpCode> parser = null;
            foreach (OpCode op in Enum.GetValues(typeof(OpCode)))
            {
                var p = Span.EqualToIgnoreCase(op.ToString()).Value(op);
                parser = parser == null ? p : parser.Or(p);
            }
            return parser;
        }

        internal static readonly TextParser<Unit> HexToken =
            from begin in Span.EqualToIgnoreCase("0x")
            from content in Numerics.HexDigits
            select Unit.Value;

        internal static readonly TextParser<Unit> PropertyToken =
            from start in Character.EqualTo('[')
            from name in Character.Except('=').IgnoreMany()
            from equal in Character.EqualTo('=')
            from val in Character.Except(']').IgnoreMany()
            from end in Character.EqualTo(']')
            select Unit.Value;

        internal static readonly TextParser<int> LabelToken =
            from content in Numerics.IntegerInt32
            //from content in Span.Regex(@"^[A-Za-z0-9_]+:") //@"^[A-Za-z][A-Za-z0-9_]+:"
            select content;

        public static readonly Tokenizer<TjsAsmToken> Instance =
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
                .Match(LabelToken, TjsAsmToken.Label, true)
                .Match(QuotedString.CStyle, TjsAsmToken.StringValue, false)
                .Match(HexToken, TjsAsmToken.Hex, true)
                .Match(Numerics.Integer, TjsAsmToken.IntValue, true)
                .Match(Numerics.Decimal, TjsAsmToken.RealValue, true)
                .Match(OctetToken, TjsAsmToken.OctetValue, false)
                .Match(OpCodeToken, TjsAsmToken.OpCode, true)
                .Match(Comment.CPlusPlusStyle, TjsAsmToken.SingleLineComment, false)
                .Match(Span.EqualTo("="), TjsAsmToken.Assign, false)
                .Match(Span.EqualTo(":"), TjsAsmToken.Colon, false)
                .Match(Span.EqualTo("("), TjsAsmToken.LBracket, false)
                .Match(Span.EqualTo(")"), TjsAsmToken.RBracket, false)
                .Match(Span.EqualTo("//"), TjsAsmToken.CommentSign, false)
                .Match(Span.EqualTo("*"), TjsAsmToken.Star, false)
                .Match(Span.EqualTo("%"), TjsAsmToken.Percent, false)
                .Match(Span.EqualTo("."), TjsAsmToken.Dot, false)
                .Match(Span.EqualTo(","), TjsAsmToken.Comma, false)
                .Match(Span.EqualTo("["), TjsAsmToken.LSBracket, false)
                .Match(Span.EqualTo("]"), TjsAsmToken.RSBracket, false)
                .Match(Span.EqualTo("<%"), TjsAsmToken.LOctetBracket, false)
                .Match(Span.EqualTo("%>"), TjsAsmToken.ROctetBracket, false)
                .Match(Span.NonWhiteSpace, TjsAsmToken.Text, true)
                .Build(); //keep last
    }
}