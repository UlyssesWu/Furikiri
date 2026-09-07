using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 对账原始 CFG 与结构化 AST 的函数终态，保守补回遗漏的公共最终返回。
    /// </summary>
    internal sealed class TerminalReturnReconciliationPass : IPass
    {
        public BlockStatement Process(
            DecompileContext context, BlockStatement statement)
        {
            if (context?.Blocks == null || context.Blocks.Count == 0 ||
                statement?.Statements == null ||
                StructuredControlFlowFacts.AlwaysTerminates(statement))
            {
                return statement;
            }

            var graph = new ControlFlowGraphAnalysis(context);
            var entry = context.Blocks.FirstOrDefault(block => block.From.Count == 0) ??
                        context.Blocks[0];
            if (!AllOriginalPathsTerminate(graph, entry))
            {
                return statement;
            }

            var pureReturns = context.Blocks
                .Where(IsPureReturnBlock)
                .OrderByDescending(block => block.Start)
                .ToList();
            if (pureReturns.Count == 0)
            {
                return statement;
            }

            // 编译器生成的函数尾 return 位于所有显式返回块之后。只接受它还
            // 有多个 CFG 前驱的唯一形态；单前驱或同位置多候选都不足以证明它
            // 是公共默认返回，宁可保留较长 AST 也不猜测返回值。
            var terminalStart = pureReturns[0].Start;
            var terminalCandidates = pureReturns
                .Where(block => block.Start == terminalStart && block.From.Count > 1)
                .ToList();
            if (terminalCandidates.Count != 1)
            {
                return statement;
            }

            var terminalReturn = terminalCandidates[0].Statements
                .OfType<ReturnExpression>().SingleOrDefault();
            if (terminalReturn == null)
            {
                return statement;
            }

            // 使用独立 ReturnExpression，公共基本块仍保留原节点供调试和后续
            // CFG 审计；返回值表达式不会在补回后再次求值于同一路径。
            statement.Statements.Add(new ExpressionStatement(
                new ReturnExpression(terminalReturn.Return)));
            return statement;
        }

        private static bool IsPureReturnBlock(Block block)
        {
            return block?.Statements?.Count > 0 &&
                   block.Statements.Count(node => node is ReturnExpression) == 1 &&
                   block.Statements.All(node =>
                       node is ReturnExpression or GotoExpression);
        }

        /// <summary>
        /// 循环会形成递归回边；在没有终止证明时直接拒绝，不把“可能无限循环”
        /// 当成显式返回。无环区域则要求每条路径先遇到 return/throw。
        /// </summary>
        private static bool AllOriginalPathsTerminate(
            ControlFlowGraphAnalysis graph, Block entry)
        {
            var memo = new Dictionary<Block, bool>();
            var visiting = new HashSet<Block>();
            bool Visit(Block block)
            {
                if (block == null)
                {
                    return false;
                }
                if (block.Statements?.Any(node =>
                        node is ReturnExpression or ThrowExpression) == true)
                {
                    return true;
                }
                if (memo.TryGetValue(block, out var cached))
                {
                    return cached;
                }

                var successors = graph.GetSuccessors(block);
                if (!visiting.Add(block) || successors.Count == 0)
                {
                    return false;
                }

                var result = successors.All(Visit);
                visiting.Remove(block);
                memo[block] = result;
                return result;
            }

            return Visit(entry);
        }
    }
}
