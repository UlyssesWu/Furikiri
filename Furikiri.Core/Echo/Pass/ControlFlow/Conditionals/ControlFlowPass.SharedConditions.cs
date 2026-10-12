using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;

namespace Furikiri.Echo.Pass
{
    partial class ControlFlowPass
    {
        /// <summary>
        /// 分支入口可能先执行赋值，再以两段以上条件选择两个完整正文。例如先清除
        /// 点击锁，随后用 `locked || link == -1` 选择处理路径。若等到叶子条件
        /// 由内向外物化，入口只会保留第一段判断，赋值后的整个 else 结构也会被
        /// 拆错。这里只提前处理“一侧继续经过纯条件、另一侧已是同级出口”的形态。
        /// </summary>
        private void StructurePayloadPrefixedConditionChains(
            bool pureDecisionDagOnly = false)
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             !candidate.Statements.IsCondition() &&
                                             candidate.Statements.GetCondition() != null &&
                                             (candidate.From.Count > 1 ||
                                             candidate.Statements
                                                  .Take(candidate.Statements.Count - 1)
                                                  .OfType<Expression>()
                                                  .Any(ExpressionEffectAnalysis
                                                      .HasObservableEffectBeyondLocalInitialization)) &&
                                             candidate.To.Count == 2 &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderBy(candidate => candidate.Start)
                         .ToList())
            {
                var continuation = root.To.FirstOrDefault(target =>
                    root.To.Any(other => other != target &&
                        other.Statements.IsCondition() &&
                        CanReachThroughConditionBlocks(other, target)));
                if (continuation == null)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    // 共享条件节点的 DAG 必须优先按到载荷的完整路径谓词恢复。
                    // 普通链识别只沿某一后继展开，会把
                    // `!call0() && (A || !call1())` 截断成 `!call0()`，并丢掉
                    // 第二次调用。DAG 候选失败后再退回线性条件链。
                    (TryStructurePureDecisionDagToPayload(root, out var logic) ||
                     (!pureDecisionDagOnly &&
                      TryStructureConditionChain(root, condition, out logic))))
                {
                    root.Statements.Replace(condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 多路分派后的独立短路判断通常有多个前驱，例如按键分派结束后统一执行
        /// `if (escape || enter || space) close()`。若先恢复外围分派，这个公共尾部
        /// 会被当成最后一个 else-if 出口，既可能丢掉部分 case，也会错误继承
        /// 外围 gate。先把非循环中的纯条件公共尾部物化，外围区域便会在它之前
        /// 正确汇合，并把尾部保留为后续的独立语句。
        /// </summary>
        private void StructureSharedTrailingConditionChains()
        {
            foreach (var root in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         block.From.Count > 1 &&
                                         block.Statements.IsCondition() &&
                                         block.To.Count == 2 &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)))
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                var condition = root.Statements.GetCondition();
                if (condition != null && IsPureConditionRegion(root) &&
                    TryStructureConditionChain(root, condition, out var logic))
                {
                    root.Statements.Replace(
                        condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 恢复 `if (A) { if (B) sideEffect(); if (C) returnValue(); } return fallback;`
        /// 这类顺序守卫。B 的载荷臂回到 C，而 C 的另一臂与 A 的假分支
        /// 共用 fallback。普通短路链恢复会把 A/B 合并为 `A &amp;&amp; B`，随后
        /// 吞掉 C 的调用条件。因此必须按 C、B、A 由内向外物化，并显式
        /// 保留两层的公共继续块。
        /// </summary>
        private void StructureSequentialNestedGuards()
        {
            foreach (var outer in _context.Blocks
                         .Where(block => !block.Hidden &&
                                         block.Statements.IsCondition() &&
                                         block.To.Count == 2 &&
                                         !_context.LoopSet.Any(loop => loop.Contains(block)))
                         .OrderByDescending(block => block.Start)
                         .ToList())
            {
                var outerCondition = outer.Statements.GetCondition();
                if (outerCondition == null)
                {
                    continue;
                }

                foreach (var first in outer.To.Where(HasPayloadArmJoiningSibling).ToList())
                {
                    // A shared body may also be reached by an earlier OR arm.
                    // It belongs to that full decision, not to this final test alone.
                    if (first.Dominator == null || !first.Dominator[outer.Id]) continue;
                    var fallback = outer.To.First(target => target != first);
                    var firstCondition = first.Statements.GetCondition();
                    var firstTrue = _context.BlockTable[firstCondition.TrueBranch];
                    var firstFalse = _context.BlockTable[firstCondition.FalseBranch];
                    var next = firstTrue.To.Contains(firstFalse) && HasBranchPayload(firstTrue)
                        ? firstFalse
                        : firstFalse.To.Contains(firstTrue) && HasBranchPayload(firstFalse)
                            ? firstTrue
                            : null;
                    var nextCondition = next?.Statements?.GetCondition();
                    if (nextCondition == null || next.To.Count != 2 ||
                        !next.To.Contains(fallback))
                    {
                        continue;
                    }

                    // C 的命中体必须是独立区域；否则可能只是普通
                    // `A &amp;&amp; B &amp;&amp; C` 短路链，不能套用本形状。
                    var guardedBody = next.To.First(target => target != fallback);
                    if (!HasBranchPayload(guardedBody) ||
                        guardedBody.Dominator == null ||
                        !guardedBody.Dominator[next.Id])
                    {
                        continue;
                    }

                    if (!StructureIfElseCore(
                            next, out var nextLogic, false, fallback))
                    {
                        continue;
                    }
                    next.Statements.Replace(
                        nextCondition, nextLogic.Simplify().ToStatement());

                    firstCondition = first.Statements.GetCondition();
                    if (firstCondition == null ||
                        !StructureIfElseCore(first, out var firstLogic, false, next))
                    {
                        continue;
                    }
                    first.Statements.Replace(
                        firstCondition, firstLogic.Simplify().ToStatement());

                    outerCondition = outer.Statements.GetCondition();
                    if (outerCondition != null &&
                        StructureIfElseCore(
                            outer, out var outerLogic, false, fallback))
                    {
                        outer.Statements.Replace(
                            outerCondition, outerLogic.Simplify().ToStatement());
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// 连续的单行成员覆盖通常共享前一个 if 的“赋值/跳过”汇合入口，因此后续
        /// 条件块会有多个前驱。外围条件若先物化，这些叶子判断会退化为裸比较，
        /// 赋值则变成无条件执行。多前驱、单臂且主体有普通载荷的约束可将它们与
        /// 只有一个条件前驱的 &&/|| 尾块区分开。
        /// </summary>
        private void StructureJoinedLeafConditions()
        {
            foreach (var block in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             (candidate.From.Count > 1 ||
                                              IsExplicitReturnJoinedLeaf(candidate)) &&
                                             candidate.Statements.GetCondition() != null &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                // 前一个候选的区域物化可能已经隐藏或替换当前块；候选列表是阶段
                // 开始时的快照，使用前必须重新确认条件节点仍然存在。
                if (block.Hidden || block.Statements.GetCondition() == null)
                {
                    continue;
                }

                var explicitReturnLeaf = IsExplicitReturnJoinedLeaf(block);
                var continuation = block.To.FirstOrDefault(candidate =>
                    block.To.Any(other => other != candidate && CanReach(other, candidate)));
                if (continuation == null)
                {
                    // 纯条件块可能是 `A && B` / `A || B` 的共享尾谓词；这里提前
                    // 物化会让外层条件失去 else。完整双臂抢救只用于三元式合流后
                    // 同块还带真实赋值等前置载荷的形态。
                    if (block.Statements.IsCondition())
                    {
                        continue;
                    }

                    // 前一条三元表达式会让后续条件拥有两个前驱；若该条件自身又是
                    // 完整 if/else，两臂不会直达彼此，而是分别给同一成员赋值后汇合。
                    // 外围区域若先物化，这个条件就会退化成裸比较，两次赋值均变成
                    // 无条件执行。仅在两臂都有载荷且明确汇合到同一后支配点时抢救。
                    var postDominator = FindIfPostDominator(block);
                    var joinedCondition = block.Statements.GetCondition();
                    var trueTarget = block.To.FirstOrDefault(target =>
                        target.Start == joinedCondition.TrueBranch) ?? block.To[0];
                    var falseTarget = block.To.FirstOrDefault(target =>
                        target.Start == joinedCondition.FalseBranch) ?? block.To[1];
                    trueTarget = NormalizeConsumedConditionalPhiTarget(trueTarget);
                    falseTarget = NormalizeConsumedConditionalPhiTarget(falseTarget);

                    if (postDominator == null || trueTarget == falseTarget ||
                        trueTarget == postDominator || falseTarget == postDominator ||
                        !HasBranchPayload(trueTarget) || !HasBranchPayload(falseTarget) ||
                        trueTarget.Statements.Any(statement =>
                            statement is ReturnExpression or ThrowExpression) ||
                        falseTarget.Statements.Any(statement =>
                            statement is ReturnExpression or ThrowExpression) ||
                        !CanReach(trueTarget, postDominator) ||
                        !CanReach(falseTarget, postDominator))
                    {
                        continue;
                    }

                    if (StructureIfElse(block, out var branchLogic))
                    {
                        block.Statements.Replace(
                            joinedCondition, branchLogic.Simplify().ToStatement());
                    }
                    continue;
                }

                var body = block.To.First(candidate => candidate != continuation);
                if (!HasBranchPayload(body) ||
                    body.Statements.Any(statement => statement is ReturnExpression or ThrowExpression))
                {
                    continue;
                }

                var condition = block.Statements.GetCondition();
                if (condition != null && StructureIfElse(block, out var logic))
                {
                    block.Statements.Replace(condition, logic.Simplify().ToStatement());

                    // 此类叶节点的父块在比较前还会更新恢复状态。若只处理叶节点，
                    // 更外层区域仍会把父条件当普通表达式收走，使公共回调变成无条件
                    // 提前返回；因此在 CFG 边仍完整时紧接着物化父条件。
                    if (explicitReturnLeaf && block.From.Count == 1)
                    {
                        var parent = block.From[0];
                        var parentCondition = parent.Hidden
                            ? null
                            : parent.Statements.GetCondition();
                        var sharedContinuation = parent.To.FirstOrDefault(
                            target => target != block);
                        if (parentCondition != null &&
                            StructureIfElseCore(
                                parent, out var parentLogic, false,
                                sharedContinuation))
                        {
                            parent.Statements.Replace(
                                parentCondition, parentLogic.Simplify().ToStatement());
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 比较仅控制一次副作用、两路随后汇合到同一个带返回值的 return 时，应先
        /// 恢复内层单臂 if。要求显式返回值可避免把普通的 `A && B` 清理守卫拆开；
        /// 后者通常汇合到公共后续语句或隐式的 `return void`。
        /// </summary>
        private bool IsExplicitReturnJoinedLeaf(Block block)
        {
            var condition = block?.Statements?.GetCondition();
            if (condition == null || !block.Statements.IsCondition() ||
                block.From.Count != 1 || block.From[0].Statements.IsCondition() ||
                block.From[0].Statements.GetCondition() == null ||
                _context.BlockTable == null ||
                !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
            {
                return false;
            }

            static bool ReturnsValue(Block candidate) =>
                candidate?.Statements?.OfType<ReturnExpression>()
                    .Any(returnExpression => returnExpression.Return != null) == true;

            bool IsSideEffectArm(Block candidate) =>
                candidate != null && HasBranchPayload(candidate) &&
                // A shared payload reached directly from the parent belongs to
                // an OR chain, not an independently nested leaf condition.
                candidate.Dominator?[block.Id] == true &&
                !candidate.Statements.Any(statement =>
                    statement is ReturnExpression or ThrowExpression);

            // 带值 return 只能由当前叶条件和其直接副作用臂拥有。若还有外层
            // 条件前驱，它是父级 Sequence 的公共出口，提前物化会隐藏其他
            // 路径仍需执行的最终 return。
            bool OwnsReturn(Block returnTarget, Block sideEffectArm) =>
                returnTarget?.From.All(predecessor =>
                    predecessor == block || predecessor == sideEffectArm) == true;

            return ReturnsValue(falseTarget) && IsSideEffectArm(trueTarget) &&
                       trueTarget.To.Contains(falseTarget) &&
                       OwnsReturn(falseTarget, trueTarget) ||
                   ReturnsValue(trueTarget) && IsSideEffectArm(falseTarget) &&
                       falseTarget.To.Contains(trueTarget) &&
                       OwnsReturn(trueTarget, falseTarget);
        }

        /// <summary>
        /// 条件链入口有时同时负责初始化局部变量，例如按键分派前的 l/r 赋值。
        /// 这使入口不再满足 Statements.IsCondition()，而后续纯条件根会被内向外
        /// 预处理抢先拆散。若入口支配一个由三条以上成功边共享的真实正文，应先
        /// 从这个带载荷入口恢复整条谓词；替换仅作用于末尾条件，前置赋值保留原位。
        /// </summary>
        private void StructurePayloadRootSharedBodyConditionChains()
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             !candidate.Statements.IsCondition() &&
                                             candidate.Statements.GetCondition() != null &&
                                             candidate.To.Count == 2 &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderBy(candidate => candidate.Start)
                         .ToList())
            {
                var sharedBody = _context.Blocks.FirstOrDefault(candidate =>
                    !candidate.Hidden && candidate != root &&
                    candidate.From.Count >= 3 &&
                    candidate.Dominator != null &&
                    candidate.Dominator[root.Id] &&
                    HasBranchPayload(candidate) &&
                    candidate.Statements.GetCondition() == null);
                if (sharedBody == null)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    TryStructureConditionChain(root, condition, out var chainLogic))
                {
                    root.Statements.Replace(
                        condition, chainLogic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 多段短路判断可能从三条以上路径汇入同一个更新块，而其父条件又位于更大
        /// 的早退区域中。若不先恢复这条链，外围 if 会把各段比较当普通表达式收走。
        /// 仅处理非循环、纯条件入口、共享块带真实载荷且由当前入口支配的形态。
        /// </summary>
        private void StructureNestedSharedBodyConditionChains()
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             candidate.Statements.IsCondition() &&
                                             candidate.From.Count >= 1 &&
                                             (candidate.From.Count > 1 ||
                                              candidate.From[0].Statements.GetCondition() != null) &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                var sharedBody = _context.Blocks.FirstOrDefault(candidate =>
                    !candidate.Hidden && candidate != root &&
                    candidate.From.Count >= 3 &&
                    candidate.Dominator != null &&
                    candidate.Dominator[root.Id] &&
                    HasBranchPayload(candidate) &&
                    candidate.Statements.GetCondition() == null);
                if (sharedBody == null)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    TryStructureConditionChain(root, condition, out var chainLogic))
                {
                    root.Statements.Replace(
                        condition, chainLogic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 局部初始化的 if/else 汇合后，后续长分派入口会有两个普通载荷前驱。
        /// 这种入口必须由外向内恢复；通用的单前驱嵌套链仍保持由内向外，避免
        /// 改变普通早退守卫的公共继续路径。
        /// </summary>
        private void StructureMultiPredecessorSharedBodyConditionChains()
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             candidate.Statements.IsCondition() &&
                                             candidate.From.Count > 1 &&
                                             candidate.From.All(predecessor =>
                                                 predecessor.Statements.GetCondition() == null) &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderBy(candidate => candidate.Start)
                         .ToList())
            {
                var sharedBody = _context.Blocks.FirstOrDefault(candidate =>
                    !candidate.Hidden && candidate != root &&
                    candidate.From.Count >= 3 &&
                    candidate.Dominator != null &&
                    candidate.Dominator[root.Id] &&
                    HasBranchPayload(candidate) &&
                    candidate.Statements.GetCondition() == null);
                if (sharedBody == null)
                {
                    continue;
                }

                var condition = root.Statements.GetCondition();
                if (condition != null &&
                    TryStructureConditionChain(root, condition, out var logic))
                {
                    root.Statements.Replace(condition, logic.Simplify().ToStatement());
                }
            }
        }

        /// <summary>
        /// 某些很长的对象过滤条件会被字节码拆成“入口条件 + 多段比较 + 共享门”。
        /// 当入口真分支和共享门真分支进入同一主体，而门的假分支直接返回时，可将
        /// 两个已经求出的谓词用 || 合并。这样能避免入口条件在外围 if 中退化为
        /// 无控制作用的裸比较，同时保留门谓词中的函数调用及短路顺序。
        /// </summary>
        private void StructureMergedBodyGateConditions()
        {
            foreach (var root in _context.Blocks
                         .Where(candidate => !candidate.Hidden &&
                                             !candidate.Statements.IsCondition() &&
                                             candidate.Statements.GetCondition() != null &&
                                             candidate.To.Count == 2 &&
                                             !_context.LoopSet.Any(loop => loop.Contains(candidate)))
                         .OrderByDescending(candidate => candidate.Start)
                         .ToList())
            {
                var rootCondition = root.Statements.GetCondition();
                var body = root.To.FirstOrDefault(target =>
                    target.Start == rootCondition.TrueBranch);
                var chainStart = root.To.FirstOrDefault(target =>
                    target.Start == rootCondition.FalseBranch);
                if (body == null || chainStart == null)
                {
                    continue;
                }

                var gate = _context.Blocks.FirstOrDefault(candidate =>
                {
                    var gateCondition = candidate.Hidden
                        ? null
                        : candidate.Statements.GetCondition();
                    if (gateCondition == null || candidate.From.Count < 4 ||
                        gateCondition.TrueBranch != body.Start ||
                        !CanReach(chainStart, candidate))
                    {
                        return false;
                    }

                    var exit = candidate.To.FirstOrDefault(target =>
                        target.Start == gateCondition.FalseBranch);
                    return exit?.Statements?.Any(statement =>
                        statement is ReturnExpression) == true;
                });
                if (gate == null)
                {
                    continue;
                }

                var gateCondition = gate.Statements.GetCondition();
                var exitBlock = gate.To.First(target =>
                    target.Start == gateCondition.FalseBranch);

                // 旧的比较链已经完整折叠进共享门谓词；断开并隐藏控制流外壳，
                // 再让普通 if 区域恢复负责主体及公共 return 的归属。
                var obsoleteChain = new HashSet<Block>();
                var pending = new Stack<Block>();
                pending.Push(chainStart);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (current == body || current == exitBlock ||
                        !obsoleteChain.Add(current))
                    {
                        continue;
                    }

                    foreach (var next in current.To)
                    {
                        pending.Push(next);
                    }
                }

                rootCondition.Condition = rootCondition.Condition.Or(
                    gateCondition.Condition);
                rootCondition.JumpIf = true;
                rootCondition.JumpTo = body.Start;
                rootCondition.ElseTo = exitBlock.Start;
                foreach (var oldTarget in root.To)
                {
                    oldTarget.From.Remove(root);
                }
                root.To = new List<Block> { body, exitBlock };
                if (!body.From.Contains(root))
                {
                    body.From.Add(root);
                }
                if (!exitBlock.From.Contains(root))
                {
                    exitBlock.From.Add(root);
                }
                foreach (var obsolete in obsoleteChain)
                {
                    obsolete.Hidden = true;
                }

                if (StructureIfElseCore(
                        root, out var mergedLogic, false, exitBlock))
                {
                    root.Statements.Replace(
                        rootCondition, mergedLogic.Simplify().ToStatement());
                }
            }
        }
    }
}
