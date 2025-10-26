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

        public void ResolveFromBlocks(List<Loop> loopSet = null, Loop currentLoop = null)
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
                
                // Check if this block is a loop header
                Loop loop = loopSet?.FirstOrDefault(l => l.Header == block);
                // Skip if this is the current loop being resolved (to avoid infinite recursion/duplication)
                if (loop != null && loop.LoopLogic != null && !loop.IsBeingResolved && loop != currentLoop)
                {
                    // Mark as being resolved to prevent infinite recursion
                    loop.IsBeingResolved = true;
                    // Add the loop statement instead of block statements
                    Statements.Add(loop.LoopLogic.ToStatement());
                    loop.IsBeingResolved = false;
                }
                else
                {
                    // Add block statements as usual
                    Statements.AddRange(block.Statements.Select(node =>
                        node is Expression exp ? new ExpressionStatement(exp) : node));
                }
            }

            Resolved = true;
        }

        public BlockStatement(Block block)
        {
            Blocks = new List<Block> {block};
        }

        public BlockStatement(List<Block> blockses, bool resolve = false)
        {
            Blocks = new List<Block>(blockses);
            if (resolve)
            {
                ResolveFromBlocks();
            }
        }

        public void AddStatement(Statement st)
        {
            Statements.Add(st);
        }
    }
}