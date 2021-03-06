﻿using System;
using System.CodeDom.Compiler;
using System.IO;

namespace Furikiri.Echo.Language
{
    class TjsTextFormatter : IFormatter, IDisposable
    {
        public IndentedTextWriter Writer { get; private set; }

        public TjsTextFormatter(IndentedTextWriter tw)
        {
            Writer = tw;
        }

        public void Write(string str)
        {
            Writer.Write(str);
        }

        public void WriteLine()
        {
            Writer.WriteLine();
        }

        public void WriteSpace()
        {
            Writer.Write(" ");
        }

        public void WriteToken(string token)
        {
            Writer.Write(token);
        }

        public void WriteComment(string comment)
        {
            StartWritingComment();
            Writer.Write(comment);
        }

        public void WriteDocumentationTag(string documentationTag)
        {
            throw new NotImplementedException();
        }

        public void WriteKeyword(string keyword)
        {
            Writer.Write(keyword);
        }

        public void WriteLiteral(string literal)
        {
            Writer.Write(literal);
        }

        public void WriteIdentifier(string value)
        {
            Writer.Write(value);
        }

        public void WriteException(string[] exceptionLines)
        {
        }

        public void Indent()
        {
            Writer.Indent++;
        }

        public void Outdent()
        {
            Writer.Indent--;
        }

        public int CurrentPosition => (Writer.InnerWriter as StringWriter)?.GetStringBuilder().Length ?? 0;

        public void StartWritingComment()
        {
            Writer.Write("// ");
        }

        public void EndWritingComment()
        {
            Writer.Write(" ");
        }

        public void WriteStartBlock()
        {
            Writer.WriteLine("{");
            Indent();
        }

        public void WriteDocumentationStartBlock()
        {
        }

        public void WriteEndBlock()
        {
            Outdent();
            Writer.WriteLine("}");
        }

        public void WriteStartUsagesBlock()
        {
        }

        public void WriteEndUsagesBlock()
        {
        }

        public event EventHandler NewLineWritten;

        public void Dispose()
        {
            Writer?.Dispose();
        }
    }
}