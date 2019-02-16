using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Furikiri.AST.Statements;
using Furikiri.Echo.Visitors;

namespace Furikiri.Echo.Language
{
    internal class TjsWriter : BaseVisitor
    {
        private IFormatter _formatter;
        private IndentedTextWriter _writer;

        public TjsWriter(TextWriter writer)
        {
            _writer = new IndentedTextWriter(writer);
            _formatter = new TjsTextFormatter(_writer);
        }

        public void WriteBlock(BlockStatement st)
        {
            Visit(st);
        }
    }
}