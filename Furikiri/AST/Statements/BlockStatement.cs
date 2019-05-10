using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.Echo;

namespace Furikiri.AST.Statements
{
    class BlockStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.BlockStatement;

        public override IEnumerable<IAstNode> Children => Statements;

        public List<Block> Blocks { get; set; }
        public List<IAstNode> Statements { get; set; } = new List<IAstNode>();

        public bool Resolved { get; set; } = false;

        public BlockStatement()
        {
        }

        public void ResolveFromBlocks()
        {
            if (Blocks == null || Blocks.Count == 0)
            {
                return;
            }

            Statements.Clear();
            foreach (var block in Blocks)
            {
                if (block.Hidden)
                {
                    continue;
                }
                Statements.AddRange(block.Statements.Select(node =>
                    node is Expression exp ? new ExpressionStatement(exp) : node));
            }

            Resolved = true;
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