using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Text;

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
            throw new NotImplementedException();
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
            throw new NotImplementedException();
        }

        public void WriteEndBlock()
        {
            Writer.WriteLine("}");
            Outdent();
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

        public void Dispose()
        {
            Writer?.Dispose();
        }
    }
}