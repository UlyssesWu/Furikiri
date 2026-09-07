using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;

namespace Furikiri.Echo.Logical
{
    class IfLogic : ILogical, IConditional
    {
        public Expression Condition { get; set; }
        public Block ConditionBlock { get; set; }
        public LogicalBlock Then { get; set; } = new LogicalBlock();
        public LogicalBlock Else { get; set; } = new LogicalBlock();
        public IfLogic ParentIf { get; set; }
        public Block PostDominator { get; set; }
        public bool IsEqualityDispatch { get; set; }
        public bool HasCompilerSwitchEvidence { get; set; }
        public bool HasCompilerDefaultEvidence { get; set; }
        public bool HasEqualityDispatchDefaultFallthrough { get; set; }
        public bool FallsThroughToEqualityDispatchDefault { get; set; }
        public bool EqualityDispatchEndsAtLoopLatch { get; set; }

        internal void HideBlocks(bool hideConditionBlock = false)
        {
            if (hideConditionBlock)
            {
                // 合成的多路 else-if 共用同一个条件根块；隐藏内层“条件块”
                // 会连同外层语句及其准备表达式一起丢失。
                if (ConditionBlock != null && ParentIf?.ConditionBlock != ConditionBlock &&
                    (ParentIf?.ConditionBlock == null ||
                     ConditionBlock.Start >= ParentIf.ConditionBlock.Start))
                {
                    // 正常的内层条件块位于父条件之后，可以安全隐藏；若条件块反而
                    // 更早，它是多路谓词恢复时复用的入口根块，隐藏会丢掉整个 if。
                    ConditionBlock.Hidden = true;
                }
            }

            Then?.HideBlocks();
            Else?.HideBlocks();
        }

        public void Invert()
        {
            Condition = Condition.Invert();
            (Then, Else) = (Else, Then);
        }

        public IfLogic Simplify()
        {
            if (Then.Statement == null && Else.IsBreak)
            {
                Invert();
                Else.Statement = null;
            }

            return this;
        }

        public Statement ToStatement()
        {
            if (Condition == null && ConditionBlock != null)
            {
                Condition = (ConditionExpression) ConditionBlock.Statements.LastOrDefault(stmt =>
                    stmt is ConditionExpression);
            }

            IfStatement i = new IfStatement(Condition, Then.ToStatement(), Else.ToStatement())
            {
                IsEqualityDispatch = IsEqualityDispatch,
                HasCompilerSwitchEvidence = HasCompilerSwitchEvidence,
                HasCompilerDefaultEvidence = HasCompilerDefaultEvidence,
                HasEqualityDispatchDefaultFallthrough =
                    HasEqualityDispatchDefaultFallthrough,
                FallsThroughToEqualityDispatchDefault =
                    FallsThroughToEqualityDispatchDefault,
                EqualityDispatchEndsAtLoopLatch =
                    EqualityDispatchEndsAtLoopLatch
            };
            if (ParentIf != null && ParentIf.PostDominator == PostDominator)
            {
                i.IsElseIf = true;
            }

            // Nested if-condition blocks may contain preparation expressions before the
            // condition itself (e.g. temp assignments). If the block is folded into an
            // else-if chain, keep those expressions immediately before the nested if.
            if (ParentIf != null && ConditionBlock != null && Condition != null)
            {
                var conditionIndex = ConditionBlock.Statements.FindIndex(
                    stmt => ReferenceEquals(stmt, Condition));
                var prefix = conditionIndex > 0
                    ? ConditionBlock.Statements.Take(conditionIndex).ToList()
                    : new System.Collections.Generic.List<IAstNode>();
                if (prefix.Count > 0)
                {
                    var block = new BlockStatement();
                    foreach (var node in prefix)
                    {
                        if (node is Statement st)
                        {
                            block.Statements.Add(st);
                        }
                        else if (node is Expression exp)
                        {
                            block.Statements.Add(new ExpressionStatement(exp));
                        }
                    }

                    block.Statements.Add(i);
                    HideBlocks();
                    return block;
                }
            }

            HideBlocks();
            return i;
        }
    }
}
