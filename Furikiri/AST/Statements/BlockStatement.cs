using System.Collections.Generic;
using System.Linq;
using Furikiri.Echo;

namespace Furikiri.AST.Statements
{
    class BlockStatement : IAstNode
    {
        public AstNodeType Type => AstNodeType.BlockStatement;

        public IEnumerable<IAstNode> Children => Statements;

        public List<Block> Blocks { get; set; }
        public List<Statement> Statements { get; set; } = new List<Statement>();

        public bool Resolved { get; set; } = false;

        public BlockStatement()
        {
        }

        public BlockStatement(Block block)
        {
            Blocks = new List<Block> {block};
        }

        public BlockStatement(List<Block> blockses)
        {
            Blocks = new List<Block>(blockses);
        }

        public void AddStatement(Statement st)
        {
            Statements.Add(st);
        }
    }
}