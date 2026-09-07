using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 只依赖结构化 AST 即可证明的控制流事实。
    /// </summary>
    internal static class StructuredControlFlowFacts
    {
        public static bool AlwaysTerminates(IAstNode node)
        {
            return node switch
            {
                ReturnExpression => true,
                ThrowExpression => true,
                ExpressionStatement
                {
                    Expression: ReturnExpression or ThrowExpression
                } => true,
                BlockStatement { Statements.Count: > 0 } block =>
                    AlwaysTerminates(block.Statements[^1]),
                IfStatement { Else: not null } conditional =>
                    AlwaysTerminates(conditional.Then) &&
                    AlwaysTerminates(conditional.Else),
                _ => false
            };
        }
    }
}
