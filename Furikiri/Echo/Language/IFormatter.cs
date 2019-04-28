using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo.Language
{
    public interface IFormatter
    {
        void Write(string str);
        void WriteLine();
        void WriteSpace();
        void WriteToken(string token);
        void WriteComment(string comment);
        void WriteDocumentationTag(string documentationTag);
        void WriteKeyword(string keyword);
        void WriteLiteral(string literal);
        void WriteDefinition(string value);
        void WriteReference(string value);
        void WriteIdentifier(string value);
        void WriteException(string[] exceptionLines);
        void Indent();
        void Outdent();
        int CurrentPosition { get; }

        /// <summary>
        /// Instructs the formatter, that all the text it recieves from now on should be handled as comment.
        /// </summary>
        void StartWritingComment();

        /// <summary>
        /// Instructs the formatter to stop handling all the text it recieves as comment and return to its default behavior.
        /// </summary>
        void EndWritingComment();

        void WriteStartBlock();
        void WriteDocumentationStartBlock();
        void WriteEndBlock();
        void WriteStartUsagesBlock();
        void WriteEndUsagesBlock();

        event EventHandler NewLineWritten;
    }
}