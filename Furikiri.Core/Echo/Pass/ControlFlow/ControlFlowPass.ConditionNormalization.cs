using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        /// <summary>
        /// 条件跳转的真、假目标完全相同时，条件结果没有控制流用途。纯条件可直接
        /// 删除；若求值中含调用或赋值则只保留该表达式的副作用。否则这种字节码
        /// 会在嵌套块中泄露成 `typeof x == "String";` 一类伪语句。
        /// </summary>
        private void NormalizeNoOpConditions()
        {
            foreach (var block in _context.Blocks)
            {
                if (block.Statements == null)
                {
                    continue;
                }

                for (var index = block.Statements.Count - 1; index >= 0; index--)
                {
                    if (block.Statements[index] is not ConditionExpression condition ||
                        condition.TrueBranch != condition.FalseBranch)
                    {
                        continue;
                    }

                    if (ExpressionEffectAnalysis.HasObservableEffect(condition.Condition))
                    {
                        block.Statements[index] = condition.Condition;
                    }
                    else
                    {
                        block.Statements.RemoveAt(index);
                    }
                }
            }
        }

        /// <summary>
        /// 表达式恢复可能已把短路比较合并进后续 ConditionExpression，但原 CFG
        /// 仍保留一层纯条件菱形。例如两条边分别直达、或经空计算块到达同一后续
        /// 条件时，这一层已不再控制任何语句。若继续按 if 结构化，会生成空 else-if
        /// 并把后续短路项从真实条件中拆掉。这里只隐藏无副作用且两侧归一化目标
        /// 完全相同的外壳；调用、赋值、自增等求值仍原样保留。
        /// </summary>
        private void HideRedundantDecisionShells()
        {
            if (_context.BlockTable == null)
            {
                return;
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in _context.Blocks)
                {
                    if (block.Hidden || !block.Statements.IsCondition() || block.To.Count != 2 ||
                        !_context.LoopSet.Any(loop => loop.Contains(block)))
                    {
                        continue;
                    }

                    var condition = block.Statements.GetCondition();
                    if (condition == null ||
                        ExpressionEffectAnalysis.HasObservableEffect(condition.Condition))
                    {
                        continue;
                    }

                    var passthrough = new HashSet<Block>();
                    var normalizedTrue = NormalizeCollapsedDecisionTarget(
                        _context.BlockTable[condition.TrueBranch], passthrough);
                    var normalizedFalse = NormalizeCollapsedDecisionTarget(
                        _context.BlockTable[condition.FalseBranch], passthrough);
                    if (normalizedTrue != normalizedFalse || passthrough.Count == 0)
                    {
                        continue;
                    }

                    block.Statements.Remove(condition);
                    block.Hidden = true;
                    foreach (var passthroughBlock in passthrough)
                    {
                        passthroughBlock.Hidden = true;
                    }
                    changed = true;
                }
            }
        }

        private void HideCollapsedPhiConditions()
        {
            if (_context.BlockTable == null)
            {
                return;
            }

            // 条件两侧都只是给 Phi 提供值并立即汇合时，表达式传播已经把它
            // 折叠为三元表达式。必须在循环体结构化前移除控制流外壳，否则
            // 其中一侧可能被误认成 continue，真正的赋值反而被跳过。
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in _context.Blocks)
                {
                    var condition = block.Hidden ? null : block.Statements.GetCondition();
                    if (condition == null ||
                        !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                        !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
                    {
                        continue;
                    }

                    var passthrough = new HashSet<Block>();
                    var hasLeadingPayload = !block.Statements.IsCondition();
                    var hasLeadingSelfAssignment = block.Statements.Any(statement =>
                        statement is BinaryExpression binary && binary.Op.CanSelfAssign() &&
                        binary.Left is LocalExpression);

                    var normalizedTrue = hasLeadingPayload
                        ? NormalizeCollapsedDecisionTarget(trueTarget, passthrough)
                        : NormalizeDecisionTarget(trueTarget, passthrough);
                    var normalizedFalse = hasLeadingPayload
                        ? NormalizeCollapsedDecisionTarget(falseTarget, passthrough)
                        : NormalizeDecisionTarget(falseTarget, passthrough);
                    if (normalizedTrue != normalizedFalse && !hasLeadingPayload)
                    {
                        var collapsedTrue = NormalizeCollapsedDecisionTarget(trueTarget, passthrough);
                        var collapsedFalse = NormalizeCollapsedDecisionTarget(falseTarget, passthrough);
                        if (collapsedTrue == collapsedFalse &&
                            collapsedTrue.Statements.OfType<Expression>().Any(expression =>
                                ContainsConditionalPhi(expression) &&
                                ConsumedConditionStatementNormalizer.ContainsEquivalent(
                                    expression, condition.Condition)))
                        {
                            normalizedTrue = collapsedTrue;
                            normalizedFalse = collapsedFalse;
                        }
                    }
                    if (normalizedTrue != normalizedFalse || passthrough.Count == 0)
                    {
                        continue;
                    }

                    var hasConditionalPhiConsumer =
                        normalizedTrue.Statements.OfType<Expression>().Any(ContainsConditionalPhi);
                    if (hasLeadingPayload && !hasLeadingSelfAssignment &&
                        !hasConditionalPhiConsumer)
                    {
                        continue;
                    }

                    // 带前置语句的扩展形态只处理已折叠进 return 的短路值，或
                    // 后继表达式中已经形成条件 Phi 的三元式。其他普通分支仍交给
                    // 原有控制流恢复，否则可能把比较结果误当左值。
                    if (hasLeadingPayload &&
                        !hasConditionalPhiConsumer &&
                        !normalizedTrue.Statements.OfType<ReturnExpression>()
                            .Any(returnExpression => returnExpression.Return != null))
                    {
                        continue;
                    }

                    if (!hasLeadingPayload)
                    {
                        // 普通纯条件块隐藏但保留条件节点，供后续迭代穿透更外层
                        // 折叠图。若后继已包含条件 Phi，条件求值已经进入三元式，
                        // 必须删除原节点，避免它被再次结构化成空 if 并抢先隐藏主体。
                        if (hasConditionalPhiConsumer)
                        {
                            block.Statements.Remove(condition);
                        }
                        block.Hidden = true;
                    }
                    else
                    {
                        // 条件前可能还有 `value += step` 一类真实语句，不能连同
                        // 条件壳一起隐藏。条件若有副作用则保留其求值，否则删除。
                        if (!hasConditionalPhiConsumer &&
                            ExpressionEffectAnalysis.HasObservableEffect(
                                condition.Condition))
                        {
                            block.Statements.Replace(condition, condition.Condition);
                        }
                        else
                        {
                            block.Statements.Remove(condition);
                        }

                        block.Hidden = block.Statements.Count == 0;
                    }
                    foreach (var passthroughBlock in passthrough)
                    {
                        passthroughBlock.Hidden = true;
                    }
                    changed = true;
                }
            }
        }

        private static bool ContainsConditionalPhi(Expression expression)
        {
            if (expression is PhiExpression { IsConditional: true })
            {
                return true;
            }

            // 多数表达式节点尚未实现 Children；显式遍历常见容器，尤其是
            // return Dictionary(... ternary ...) 这类条件 Phi 位于调用参数中的形态。
            return expression switch
            {
                ReturnExpression ret => ContainsConditionalPhi(ret.Return),
                InvokeExpression invoke => ContainsConditionalPhi(invoke.Instance) ||
                                           ContainsConditionalPhi(invoke.MethodExpression) ||
                                           invoke.Parameters.Any(ContainsConditionalPhi),
                BinaryExpression binary => ContainsConditionalPhi(binary.Left) ||
                                           ContainsConditionalPhi(binary.Right),
                UnaryExpression unary => ContainsConditionalPhi(unary.Target),
                ConditionExpression condition => ContainsConditionalPhi(condition.Condition),
                PropertyAccessExpression property => ContainsConditionalPhi(property.Instance) ||
                                                     ContainsConditionalPhi(property.Property),
                _ => expression?.Children?.OfType<Expression>()
                    .Any(ContainsConditionalPhi) == true
            };
        }

        /// <summary>
        /// 三元 Phi 已进入 return/调用参数后，产生它的条件菱形只剩隐藏外壳。
        /// 外层 if 的单侧 CFG 边仍指向该外壳时，可穿透到真正的返回块；仅在目标
        /// 确实含条件 Phi 时归一化，避免改变默认参数初始化等普通隐藏条件。
        /// </summary>
        private Block NormalizeConsumedConditionalPhiTarget(Block target)
        {
            if (target == null || !target.Hidden)
            {
                return target;
            }

            var passthrough = new HashSet<Block>();
            // HideCollapsedPhiConditions 会在确认条件已进入 Phi 后删除条件节点，
            // 因而这里既要穿透“仍有条件的隐藏菱形”，也要穿透“已清空的隐藏菱形”。
            var normalized = NormalizeCollapsedDecisionTarget(target, passthrough);
            if (normalized == target ||
                !normalized.Statements.OfType<Expression>().Any(ContainsConditionalPhi))
            {
                return target;
            }

            foreach (var block in passthrough)
            {
                block.Hidden = true;
            }
            return normalized;
        }

        /// <summary>
        /// 沿无副作用的纯条件块和空跳板寻找最终目标。表达式传播把短路结果折叠
        /// 进后继表达式后，原 CFG 仍会留下这类条件外壳；仅在所有路径汇到同一
        /// 目标时才穿透，避免把真正控制副作用的 if 当成可删除条件。
        /// </summary>
        private static Block NormalizeCollapsedDecisionTarget(
            Block block, HashSet<Block> passthroughBlocks)
        {
            var memo = new Dictionary<Block, Block>();
            var visiting = new HashSet<Block>();

            Block Normalize(Block current)
            {
                if (current == null || !visiting.Add(current))
                {
                    return current;
                }

                if (memo.TryGetValue(current, out var cached))
                {
                    visiting.Remove(current);
                    return cached;
                }

                var result = current;
                if ((current.Statements.Count == 0 || IsDecisionPassthrough(current)) &&
                    current.To.Count == 1)
                {
                    passthroughBlocks?.Add(current);
                    result = Normalize(current.To[0]);
                }
                else if ((current.Statements.IsCondition() || current.Hidden && current.Statements.Count == 0) &&
                         current.To.Count == 2)
                {
                    var condition = current.Statements.GetCondition();
                    var normalizedFirst = Normalize(current.To[0]);
                    var normalizedSecond = Normalize(current.To[1]);
                    var consumedByPhi = normalizedFirst == normalizedSecond &&
                        normalizedFirst.Statements.OfType<Expression>().Any(expression =>
                            ContainsConditionalPhi(expression) &&
                            ConsumedConditionStatementNormalizer.ContainsEquivalent(
                                expression, condition?.Condition));
                    if (!ExpressionEffectAnalysis.HasObservableEffect(condition?.Condition) || consumedByPhi)
                    {
                        if (normalizedFirst == normalizedSecond)
                        {
                            passthroughBlocks?.Add(current);
                            result = normalizedFirst;
                        }
                    }
                }

                visiting.Remove(current);
                memo[current] = result;
                return result;
            }

            return Normalize(block);
        }
    }
}
