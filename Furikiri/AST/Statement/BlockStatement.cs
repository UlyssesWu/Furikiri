using System.Collections.Generic;

namespace Furikiri.AST.Statement
{
    class BlockStatement : IAstNode
    {
        public AstNodeType Type => AstNodeType.BlockStatement;
        public List<IAstNode> Children { get; set; } = new List<IAstNode>();
    }
}