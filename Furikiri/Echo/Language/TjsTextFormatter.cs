using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Furikiri.Echo.Language
{
    class TjsTextFormatter : IFormatter
    {
        public TextWriter Writer { get; private set; }

        public TjsTextFormatter(TextWriter tw)
        {
            Writer = tw;
        }

        public int IndentLength { get; set; } = 0;

        public string Indenter { get; internal set; } = "    ";

        public void Write(string str)
        {
            throw new NotImplementedException();
        }

        public void WriteLine()
        {
            throw new NotImplementedException();
        }

        public void WriteSpace()
        {
            throw new NotImplementedException();
        }

        public void WriteToken(string token)
        {
            throw new NotImplementedException();
        }

        public void WriteComment(string comment)
        {
            throw new NotImplementedException();
        }

        public void WriteDocumentationTag(string documentationTag)
        {
            throw new NotImplementedException();
        }

        public void WriteKeyword(string keyword)
        {
            throw new NotImplementedException();
        }

        public void WriteLiteral(string literal)
        {
            throw new NotImplementedException();
        }

        public void WriteDefinition(string value, object definition)
        {
            throw new NotImplementedException();
        }

        public void WriteReference(string value, object reference)
        {
            throw new NotImplementedException();
        }

        public void WriteIdentifier(string value, object identifier)
        {
            throw new NotImplementedException();
        }

        public void WriteException(string[] exceptionLines)
        {
            throw new NotImplementedException();
        }

        public void Indent()
        {
            throw new NotImplementedException();
        }

        public void Outdent()
        {
            throw new NotImplementedException();
        }

        public int CurrentPosition { get; }

        public void StartWritingComment()
        {
            throw new NotImplementedException();
        }

        public void EndWritingComment()
        {
            throw new NotImplementedException();
        }

        public void WriteStartBlock()
        {
            throw new NotImplementedException();
        }

        public void WriteDocumentationStartBlock()
        {
            throw new NotImplementedException();
        }

        public void WriteEndBlock()
        {
            throw new NotImplementedException();
        }

        public void WriteNamespaceStartBlock()
        {
            throw new NotImplementedException();
        }

        public void WriteNamespaceEndBlock()
        {
            throw new NotImplementedException();
        }

        public void WriteStartUsagesBlock()
        {
            throw new NotImplementedException();
        }

        public void WriteEndUsagesBlock()
        {
            throw new NotImplementedException();
        }

        public event EventHandler NewLineWritten;
    }
}