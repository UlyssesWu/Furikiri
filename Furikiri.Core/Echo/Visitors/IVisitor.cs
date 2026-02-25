using System;
using System.Collections.Generic;
using System.Text;
using Furikiri.AST;

namespace Furikiri.Echo.Visitors
{
    interface IVisitor
    {
        void Visit(IAstNode node);
    }
}