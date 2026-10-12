using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Echo.Logical;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        /// <summary>
        /// 分支区域中的短路链也必须先从最早入口整体恢复。若直接从尾部逐个
        /// 物化普通 if，A &amp;&amp; call0() || B &amp;&amp; call1() 会被拆成嵌套
        /// if/else，共同继续块还可能被误收入最后一个 false 分支。
        /// </summary>
        private void StructureNestedConditionChains(
            IEnumerable<Block> candidates, ISet<Block> excluded = null)
        {
            foreach (var candidate in candidates
                         .Distinct()
                         .Where(block => !block.Hidden &&
                                         (excluded == null || !excluded.Contains(block)))
                         .OrderBy(block => block.Start)
                         .ToList())
            {
                // 前面的根条件可能已经把本块吸收到同一条短路链并隐藏。
                // 枚举快照仍包含它，若再次结构化会重复拼接条件并破坏共享分支。
                if (candidate.Hidden)
                {
                    continue;
                }

                var nestedCondition = candidate.Statements.GetCondition();
                if (nestedCondition != null &&
                    TryStructureConditionChain(
                        candidate, nestedCondition, out var nestedChain))
                {
                    candidate.Statements.Replace(
                        nestedCondition, nestedChain.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 单臂条件的主体区域中可能还有“内层判断汇合后继续清理”的结构，
        /// 如 `if (A) { if (B) update(); cleanup(); }`。先物化这类具有不同
        /// 立即后支配点的内层判断，防止外围区域把 B 和 update/cleanup 顺序复制。
        /// 共享同一汇合点且有一条边直达汇合点的块仍属于 &&/|| 短路链，留给
        /// 后续链式恢复，避免把 `A || B` 错拆成反向嵌套判断。
        /// </summary>
        private void StructureStrictlyNestedDecisions(IEnumerable<Block> candidates)
        {
            var candidateSet = candidates.Distinct().ToHashSet();
            foreach (var outer in candidateSet
                         .Where(block => !block.Hidden && block.Statements.GetCondition() != null)
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                if (outer.Hidden)
                {
                    continue;
                }

                var continuation = outer.To.FirstOrDefault(candidate =>
                    outer.To.Any(other => other != candidate && CanReach(other, candidate)));
                if (continuation == null ||
                    (outer.From.Count == 1 &&
                     outer.From[0].Statements.GetCondition() != null &&
                     (FindIfPostDominator(outer.From[0]) == continuation ||
                      outer.From[0].To.Any(target => target == continuation))))
                {
                    continue;
                }

                var body = outer.To.First(block => block != continuation);
                Block nestedRoot = null;
                if (body.Hidden)
                {
                    // 条件 Phi 已把前半段谓词折叠进后继表达式时，原入口会成为
                    // 隐藏决策壳。只有两条路径确实归一到同一条件块才穿透它。
                    var passthrough = new HashSet<Block>();
                    var normalized = NormalizeCollapsedDecisionTarget(body, passthrough);
                    if (normalized != body && normalized.Statements.GetCondition() != null)
                    {
                        nestedRoot = normalized;
                    }
                }
                else if (body.Statements.GetCondition() != null)
                {
                    var innerContinuation = body.To.FirstOrDefault(candidate =>
                        body.To.Any(other => other != candidate && CanReach(other, candidate)));
                    innerContinuation ??= FindIfPostDominator(body);
                    var hasTwoPayloadArms = body.To.Count == 2 &&
                        body.To.All(target => target != continuation &&
                                              target.Dominator?[body.Id] == true &&
                                              HasBranchPayload(target));
                    if (innerContinuation != null &&
                        ((innerContinuation != continuation &&
                          innerContinuation.PostDominator != null &&
                          innerContinuation.PostDominator[continuation.Id]) ||
                         (innerContinuation == continuation && hasTwoPayloadArms)))
                    {
                        nestedRoot = body;
                    }
                }

                if (nestedRoot == null)
                {
                    continue;
                }

                // 提前 return 的布尔链应由专用谓词恢复整体处理；先拆其中一段会
                // 改变 default return 或公共继续路径。这里只抢救带普通副作用和
                // 清理尾部的嵌套守卫。
                if (CanReachReturnBefore(nestedRoot, continuation))
                {
                    continue;
                }

                var nestedCondition = nestedRoot.Statements.GetCondition();
                if (nestedCondition != null &&
                    StructureIfElse(nestedRoot, out var nestedLogic))
                {
                    nestedRoot.Statements.Replace(
                        nestedCondition, nestedLogic.Simplify().ToStatement());
                }

                var outerCondition = outer.Statements.GetCondition();
                if (outerCondition != null && StructureIfElse(outer, out var outerLogic))
                {
                    outer.Statements.Replace(
                        outerCondition, outerLogic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 单臂分支入口可能同时包含准备语句和另一个条件，例如短路表达式
        /// <c>target &amp;&amp; (name = target.name) != ""</c> 的第二段。外层快速路径
        /// 只收集入口块，因此要先把入口后的受控区域折叠为嵌套 IfStatement。
        /// </summary>
        private void StructureNestedBranchCondition(Block branch)
        {
            var nestedCondition = branch?.Statements.GetCondition();
            if (nestedCondition != null && StructureIfElse(branch, out var nestedLogic))
            {
                branch.Statements.Replace(
                    nestedCondition, nestedLogic.Simplify().ToStatement());
            }
        }

        /// <summary>
        /// 内层 if 先物化时会隐藏自己的 then/else 数据块。外层条件若某一路正好
        /// 复用内层的一个分支，不能简单穿过隐藏块到公共尾部，否则该路语句会丢失。
        /// 例如 C ? (N ? A : B) : B 可安全合并为 (C &amp;&amp; N) ? A : B。
        /// </summary>
        private bool TryMergeConsumedNestedBranch(
            Block owner, ConditionExpression outerCondition,
            Block trueBlock, Block falseBlock, Block postDominator,
            out IfLogic logic)
        {
            logic = null;
            IfLogic mergedLogic = null;
            if (trueBlock == null || falseBlock == null ||
                (!trueBlock.Hidden && !falseBlock.Hidden))
            {
                return false;
            }

            var trueOwner = NormalizeDecisionTarget(trueBlock, new HashSet<Block>());
            var falseOwner = NormalizeDecisionTarget(falseBlock, new HashSet<Block>());

            bool TryBuild(Block nestedOwner, Block sharedBlock, bool nestedOnTrue)
            {
                var nested = nestedOwner?.Statements?.OfType<IfStatement>().LastOrDefault();
                if (nested == null)
                {
                    return false;
                }

                var nestedCondition = nested.Condition is ConditionExpression wrapper
                    ? wrapper.Condition
                    : nested.Condition;
                var outer = outerCondition.Condition;
                Expression combined;
                Statement thenStatement;
                Statement elseStatement;

                if (ContainsAnyOriginalNode(nested.Else, sharedBlock))
                {
                    // C ? (N ? A : B) : B  =>  (C && N) ? A : B
                    combined = nestedOnTrue
                        ? outer.And(nestedCondition)
                        : outer.Invert().And(nestedCondition);
                    thenStatement = nested.Then;
                    elseStatement = nested.Else;
                }
                else if (ContainsAnyOriginalNode(nested.Then, sharedBlock))
                {
                    // C ? (N ? A : B) : A  =>  (!C || N) ? A : B
                    combined = nestedOnTrue
                        ? outer.Invert().Or(nestedCondition)
                        : outer.Or(nestedCondition);
                    thenStatement = nested.Then;
                    elseStatement = nested.Else;
                }
                else
                {
                    return false;
                }

                mergedLogic = new IfLogic
                {
                    ConditionBlock = owner,
                    Condition = combined,
                    PostDominator = postDominator,
                    Then = { Type = LogicalBlockType.Statement, Statement = thenStatement },
                    Else = { Type = LogicalBlockType.Statement, Statement = elseStatement }
                };
                nestedOwner.Hidden = true;
                return true;
            }

            var merged = trueBlock.Hidden && TryBuild(trueOwner, falseBlock, true) ||
                         falseBlock.Hidden && TryBuild(falseOwner, trueBlock, false);
            logic = mergedLogic;
            return merged;
        }

        private static bool ContainsAnyOriginalNode(Statement statement, Block sourceBlock)
        {
            if (statement == null || sourceBlock?.Statements == null)
            {
                return false;
            }

            bool Contains(IAstNode node)
            {
                if (node == null)
                {
                    return false;
                }

                if (sourceBlock.Statements.Any(source =>
                        ReferenceEquals(source, node) ||
                        node is ExpressionStatement expressionStatement &&
                        ReferenceEquals(source, expressionStatement.Expression)))
                {
                    return true;
                }

                return node switch
                {
                    BlockStatement block => block.Statements.Any(Contains),
                    IfStatement nested => Contains(nested.Then) || Contains(nested.Else),
                    _ => false
                };
            }

            return Contains(statement);
        }
    }
}
