using System;
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
        private bool GetThenElseBlock(ConditionExpression condition, List<Block> blocks, out Block then,
            out Block @else)
        {
            if (blocks.Count < 2)
            {
                then = null;
                @else = null;
                return false;
            }

            Block toBlock = blocks.FirstOrDefault(b => b.Start == condition.JumpTo);
            Block elBlock = blocks.FirstOrDefault(b => b.Start == condition.ElseTo);
            if (toBlock != null && elBlock != null)
            {
                if (condition.JumpIf)
                {
                    then = toBlock;
                    @else = elBlock;
                }
                else
                {
                    then = elBlock;
                    @else = toBlock;
                }

                return true;
            }

            then = blocks[0];
            @else = blocks[1];
            return false;
        }

        private void MergeIfCondition(IfLogic logic, ConditionExpression condition)
        {
            if (condition == null || _context?.BlockTable == null ||
                !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueBlock) ||
                !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseBlock))
            {
                return;
            }

            //recursive to final condition and go back
            Expression exp = null;
            MergeIfCondition(condition, logic.ConditionBlock, logic.PostDominator, ref exp, ref trueBlock, ref falseBlock);
            if (exp != null)
            {
                // Unwrap any ConditionExpression wrapper to prevent self-referential cycles.
                // merge/exp may be a ConditionExpression from the base case; unwrap to its
                // inner condition to avoid setting condi.Condition = condi.
                while (exp is ConditionExpression unwrap)
                {
                    exp = unwrap.Condition;
                }

                if (logic.Condition is ConditionExpression condi)
                {
                    condi.Condition = exp;
                }
                else
                {
                    logic.Condition = exp;
                }

                logic.Then.Blocks = new List<Block> {trueBlock};
                logic.Else.Blocks = new List<Block> {falseBlock};

                // Remove standalone expression from the condition block that is now
                // part of the merged condition (e.g., a CALL result used as || operand).
                // Only the first operand (leftmost leaf of the || chain) can be in the
                // condition block as a standalone expression; others are in hidden blocks.
                if (logic.ConditionBlock != null)
                {
                    var mergedExpr = (logic.Condition is ConditionExpression ce) ? ce.Condition : exp;
                    var leftmost = mergedExpr;
                    while (leftmost is BinaryExpression bin &&
                           (bin.Op == BinaryOp.LogicOr || bin.Op == BinaryOp.LogicAnd))
                    {
                        leftmost = bin.Left;
                    }

                    var condIdx = logic.ConditionBlock.Statements.IndexOf(condition);
                    if (condIdx > 0)
                    {
                        var prev = logic.ConditionBlock.Statements[condIdx - 1];
                        if (prev is Expression prevExpr && ReferenceEquals(prevExpr, leftmost))
                        {
                            logic.ConditionBlock.Statements.RemoveAt(condIdx - 1);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 将一串只负责跳转的条件块一次性恢复为“到达目标体”的谓词。
        /// 例如 A 为真返回、否则检查 B，再检查 C，可直接得到 A || (B && C)，
        /// 避免逐块递归时重复改写共享条件树。
        /// </summary>
        private bool TryStructureConditionChain(
            Block root, ConditionExpression rootCondition, out IfLogic logic,
            bool multiWayOnly = false)
        {
            logic = null;
            if (_context.BlockTable == null)
            {
                return false;
            }

            if (TryStructureEqualitySwitchChain(root, out logic))
            {
                return true;
            }

            var chainPostDominator = FindIfPostDominator(root);
            var containingLoop = _context.LoopSet
                .Where(candidate => candidate.Contains(root))
                .OrderBy(candidate => candidate.Blocks.Count).FirstOrDefault();
            if (containingLoop != null)
            {
                var boundary = IfRegionAnalyzer.Analyze(_graph, root,
                    _context.BlockTable[rootCondition.TrueBranch],
                    _context.BlockTable[rootCondition.FalseBranch], chainPostDominator,
                    containingLoop.Header,
                    candidate => candidate.Statements.Any(statement => statement is
                        ReturnExpression or ThrowExpression),
                    candidate => candidate == containingLoop.Header ||
                        IsContinueTarget(candidate, containingLoop));
                // A hidden Phi shell is part of the same decision, not its
                // normal continuation. Its visible condition can share the
                // success payload with an earlier OR arm.
                if (NormalizeConsumedConditionalPhiTarget(boundary.NormalContinuation) ==
                    boundary.NormalContinuation)
                    chainPostDominator = boundary.NormalContinuation;
            }
            var passthroughBlocks = new HashSet<Block>();
            var conditionBlocks = new HashSet<Block> { root };
            var terminals = new HashSet<Block>();
            var pending = new Stack<Block>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var current = pending.Pop();
                var condition = current.Statements.GetCondition();
                if (condition == null ||
                    !_context.BlockTable.TryGetValue(condition.TrueBranch, out var trueTarget) ||
                    !_context.BlockTable.TryGetValue(condition.FalseBranch, out var falseTarget))
                {
                    return false;
                }

                trueTarget = NormalizeConditionChainTarget(trueTarget, passthroughBlocks);
                falseTarget = NormalizeConditionChainTarget(falseTarget, passthroughBlocks);

                foreach (var pair in new[]
                         {
                             (Target: trueTarget, Sibling: falseTarget),
                             (Target: falseTarget, Sibling: trueTarget)
                         })
                {
                    var target = pair.Target;
                    InlineShortCircuitAssignment(target);
                    // 根条件与后继条件直接共享同一个载荷块时，两者共同组成
                    // 短路谓词，例如 `x === void || x > min`。后继的另一条边
                    // 即使进入下一条顺序 guard，也不能因此把当前 OR 拆成
                    // `x !== void && x > min`。兄弟仍是条件块的形态则确实可能是
                    // 两个顺序守卫，继续使用下面的保守排除规则。
                    var sharesDirectPayloadTarget =
                        pair.Sibling.Statements.GetCondition() == null &&
                        HasBranchPayload(pair.Sibling) &&
                        target.To.Contains(pair.Sibling);
                    // 中间短路块必须只有条件本身；带准备语句的块是实际分支体。
                    // 此外它还必须能只经过条件块回到同级另一出口。这个约束会把
                    // 分支体开头的普通嵌套 if 排除在短路链之外。
                    if (target != root && target != chainPostDominator &&
                        target.Statements.IsCondition() &&
                        target.From.All(predecessor =>
                            predecessor.Statements.GetCondition() != null ||
                            predecessor.Statements.Count == 0 ||
                            IsDecisionPassthrough(predecessor)) &&
                        // 内层载荷落入兄弟条件通常表示两个顺序守卫，不能合并。
                        // 但两侧若不穿过彼此就能到达同一载荷，它们属于同一个
                        // 判定 DAG（典型为 `(A && B) || (C && D)`），仍须整体恢复。
                        // 兄弟块若同时是根的直接出口和后支配点，则属于公共继续流。
                        (!HasPayloadArmJoiningSibling(target) ||
                         sharesDirectPayloadTarget ||
                         SharePayloadArmWithoutCrossing(
                             target, pair.Sibling, chainPostDominator) ||
                         pair.Sibling == chainPostDominator &&
                         root.To.Contains(pair.Sibling)) &&
                        (CanReachThroughConditionBlocks(target, pair.Sibling) ||
                         sharesDirectPayloadTarget ||
                         SharePayloadArmWithoutCrossing(
                             target, pair.Sibling, chainPostDominator)))
                    {
                        // 菱形条件的共享节点可能先从一条边被暂记为终点，随后才
                        // 从另一条边证明它仍是条件链的一部分。升级为条件块时必须
                        // 撤销旧终点；否则同一块同时出现在 conditions/terminals，
                        // 二路短路表达式会被误判成三路分派并拆坏分支赋值。
                        terminals.Remove(target);
                        if (conditionBlocks.Add(target))
                        {
                            pending.Push(target);
                        }
                    }
                    else if (!conditionBlocks.Contains(target))
                    {
                        terminals.Add(target);
                    }
                }
            }

            // 单个条件不需要走链式恢复。
            if (conditionBlocks.Count <= 1 || terminals.Count < 2)
            {
                return false;
            }

            var terminalList = terminals.ToList();
            if (multiWayOnly && terminalList.Count <= 2)
            {
                return false;
            }

            if (terminalList.Count > 2)
            {
                var multiWay = TryStructureMultiWayConditionChain(
                    root, conditionBlocks, terminalList, passthroughBlocks, out logic);
                if (multiWay) return true;

                // A shared success entry can begin its own conditional body.
                // Recover that strictly nested region before retrying the outer
                // predicate; structuring an OR arm first would lose the other
                // incoming path to the shared entry.
                foreach (var nested in conditionBlocks.Where(candidate =>
                             candidate != root && !candidate.Hidden &&
                             candidate.Dominator?[root.Id] == true)
                         .OrderBy(candidate => candidate.Start))
                {
                    var join = FindIfPostDominator(nested);
                    var condition = nested.Statements.GetCondition();
                    if (condition == null || join == null || join == chainPostDominator ||
                        join == nested || join.Dominator?[nested.Id] != true)
                        continue;
                    if (TryStructureConditionChain(nested, condition, out var nestedLogic))
                    {
                        nested.Statements.Replace(condition, nestedLogic.Simplify().ToStatement());
                        return TryStructureConditionChain(root, rootCondition, out logic, multiWayOnly);
                    }
                }
                return false;
            }

            Block bodyTarget;
            Block continuationTarget;
            Block branchPostDominator = null;
            var hasElseBranch = false;
            var bodyIsLoopBreak = false;
            var bodyIsLoopExitRegion = false;
            var continuationCompletesLoopIteration = false;
            var returnTargets = terminalList
                .Where(target => target.Statements.Any(statement => statement is ReturnExpression) ||
                                 FindIfPostDominator(target)?.Statements.Any(
                                     statement => statement is ReturnExpression) == true)
                .ToList();
            var pureReturnTargets = returnTargets
                .Where(target => target.Statements.All(statement =>
                    statement is ReturnExpression || statement is GotoExpression))
                .ToList();

            // 父循环的 Blocks 也包含所有子循环块。条件属于嵌套循环时必须选
            // 最内层循环，否则内层回边会被当成普通可达路径，两个出口看起来
            // 互相可达，完整短路链便无法恢复。
            var externalLoopTargets = containingLoop == null
                ? new List<Block>()
                : terminalList.Where(target => !containingLoop.Contains(target)).ToList();
            var internalLoopTargets = containingLoop == null
                ? new List<Block>()
                : terminalList.Where(containingLoop.Contains).ToList();

            // 循环内判断两个分支是否为“执行体 -> 公共尾部”时，只考察当前迭代。
            // 若沿回边进入下一轮，公共尾部当然也能再次到达执行体，会把实际单向
            // 关系误判成环并放弃整条短路条件链。
            var firstReachesSecond = containingLoop == null
                ? CanReach(terminalList[0], terminalList[1])
                : CanReachWithoutLoopBackEdge(terminalList[0], terminalList[1], containingLoop.Header);
            var secondReachesFirst = containingLoop == null
                ? CanReach(terminalList[1], terminalList[0])
                : CanReachWithoutLoopBackEdge(terminalList[1], terminalList[0], containingLoop.Header);
            if (firstReachesSecond && secondReachesFirst)
            {
                return false;
            }

            var loopContinueTargets = containingLoop == null
                ? new List<Block>()
                : terminalList.Where(target => target == containingLoop.Header ||
                                                IsContinueTarget(target, containingLoop))
                    .ToList();

            var containingLoopBreak = containingLoop?.Break ??
                                      (containingLoop?.LoopLogic as DoWhileLogic)?.Break ??
                                      (containingLoop == null ? null : FindBreak(containingLoop));

            // 条件链的一侧离开循环、另一侧回到闩锁时，外部目标是 break。
            // 普通可达性会沿下一轮循环最终到达退出块，因而不能用“可达”把它
            // 误判成公共尾部，否则 break 会退化成空 if。该分支也可能先执行
            // 多个基本块再退出循环，此时要把完整区域连同 break 一起收入正文。
            if (externalLoopTargets.Count == 1 && internalLoopTargets.Count == 1 &&
                (IsLoopBreakArm(externalLoopTargets[0], containingLoopBreak, containingLoop) ||
                 AllPathsReachLoopExitOrTerminate(
                     externalLoopTargets[0], containingLoopBreak, containingLoop)))
            {
                bodyTarget = externalLoopTargets[0];
                continuationTarget = internalLoopTargets[0];
                bodyIsLoopBreak = IsLoopBreakArm(
                    bodyTarget, containingLoopBreak, containingLoop);
                // 自然循环不会把终止函数的 return 块纳入 Blocks，因此它同样会
                // 出现在 externalLoopTargets 中，但语义不是“执行一段区域后 break”。
                // 直接 return 应保留为普通条件体；若按循环退出区域收集，找不到
                // 通往 break 的支配区域就会放弃整条短路谓词。
                bodyIsLoopExitRegion = !bodyIsLoopBreak &&
                                       !TerminalConditionChainPlanner
                                           .IsSimpleTerminalReturnBlock(bodyTarget);
            }
            // for 提升会把空闩锁归一化为循环头。此时循环头表示“条件未命中，
            // 继续下一轮”，绝不能被选成 if 主体，否则谓词会反转且真实副作用
            // 被提升为无条件语句。
            else if (loopContinueTargets.Count == 1 && terminalList.Count == 2)
            {
                continuationTarget = loopContinueTargets[0];
                bodyTarget = terminalList.First(target => target != continuationTarget);
                continuationCompletesLoopIteration = true;
            }
            // 一侧最终落入另一侧时，后者是公共尾部，即使它恰好只有 return
            // 也不能当成提前返回守卫，否则有副作用的可选分支会跳过公共返回。
            else if (firstReachesSecond || secondReachesFirst)
            {
                bodyTarget = firstReachesSecond ? terminalList[0] : terminalList[1];
                continuationTarget = firstReachesSecond ? terminalList[1] : terminalList[0];
            }
            else if (pureReturnTargets.Count == 1)
            {
                var returnTarget = pureReturnTargets[0];
                var sharedReturn = IsSharedVoidContinuation(returnTarget) &&
                    returnTarget.From.Any(predecessor =>
                    !conditionBlocks.Contains(predecessor) &&
                    !passthroughBlocks.Contains(predecessor));
                // A shared implicit return is also the normal end of other
                // switch arms. Keep it visible instead of claiming it as this
                // decision's exclusive early-return body.
                continuationTarget = sharedReturn
                    ? returnTarget : terminalList.First(target => target != returnTarget);
                bodyTarget = sharedReturn
                    ? terminalList.First(target => target != returnTarget) : returnTarget;
            }
            else
            {
                // 两个出口互不可达但共同汇合，是带 else 的短路条件。
                // 字节码通常先排列源码的 then 区域，因此用较早的入口作为正分支，
                // 再根据“到达该入口”的路径反推出完整的 && / || 谓词。
                branchPostDominator = FindIfPostDominator(root);
                if (branchPostDominator == null || terminals.Contains(branchPostDominator))
                {
                    return false;
                }

                bodyTarget = terminalList.OrderBy(target => target.Start).First();
                continuationTarget = terminalList.First(target => target != bodyTarget);
                hasElseBranch = true;
            }

            var memo = new Dictionary<Block, PathPredicate>();
            PathPredicate Build(Block current)
            {
                if (current == bodyTarget)
                {
                    return PathPredicate.True();
                }

                if (current == continuationTarget)
                {
                    return PathPredicate.False();
                }

                if (!conditionBlocks.Contains(current))
                {
                    return null;
                }

                if (memo.TryGetValue(current, out var cached))
                {
                    return cached;
                }

                var condition = current.Statements.GetCondition();
                var whenTrue = Build(NormalizeConditionChainTarget(
                    _context.BlockTable[condition.TrueBranch], passthroughBlocks));
                var whenFalse = Build(NormalizeConditionChainTarget(
                    _context.BlockTable[condition.FalseBranch], passthroughBlocks));
                if (whenTrue == null || whenFalse == null)
                {
                    return null;
                }

                var combined = PathPredicate.Combine(condition.Condition, whenTrue, whenFalse);
                memo[current] = combined;
                return combined;
            }

            var predicate = Build(root);
            if (predicate?.Expression == null || predicate.Constant != null)
            {
                return false;
            }

            IfLogic result;
            if (hasElseBranch)
            {
                var initialThen = CollectDominatedBranchRegion(
                    root, bodyTarget, branchPostDominator, true);
                var initialElse = CollectDominatedBranchRegion(
                    root, continuationTarget, branchPostDominator, true);
                if (initialThen.Count == 0 || initialElse.Count == 0)
                {
                    return false;
                }

                var nestedCandidates = initialThen.Concat(initialElse)
                    .Distinct()
                    .Where(candidate => !conditionBlocks.Contains(candidate))
                    .ToList();
                StructureNestedConditionChains(nestedCandidates, conditionBlocks);

                // 外层条件物化前先恢复两个分支区域里的内层条件，否则这些条件块
                // 随区域一起隐藏后只会留下裸比较表达式。
                foreach (var candidate in nestedCandidates
                             .OrderByDescending(candidate => candidate.Start))
                {
                    var nestedCondition = candidate.Statements.GetCondition();
                    if (nestedCondition != null && StructureIfElse(candidate, out var nestedLogic))
                    {
                        candidate.Statements.Replace(
                            nestedCondition, nestedLogic.Simplify().ToStatement());
                    }
                }

                var thenRegion = CollectDominatedBranchRegion(
                    root, bodyTarget, branchPostDominator, true);
                var elseRegion = CollectDominatedBranchRegion(
                    root, continuationTarget, branchPostDominator, true);
                if (thenRegion.Count == 0 || elseRegion.Count == 0)
                {
                    return false;
                }

                result = new IfLogic
                {
                    ConditionBlock = root,
                    Condition = predicate.Expression,
                    PostDominator = branchPostDominator,
                    Then = { Type = LogicalBlockType.BlockList, Blocks = thenRegion },
                    Else = { Type = LogicalBlockType.BlockList, Blocks = elseRegion }
                };
            }
            else
            {
                if (continuationCompletesLoopIteration &&
                    bodyTarget.Dominator?[root.Id] != true)
                {
                    // The normal successor also has incoming paths outside this
                    // guard. Preserve it as a shared tail instead of consuming it
                    // as this condition's body. Only the transfer arm is owned here.
                    result = new IfLogic
                    {
                        ConditionBlock = root,
                        Condition = predicate.Expression.Invert(),
                        PostDominator = bodyTarget,
                        Then = { Type = LogicalBlockType.Statement, Statement = new ContinueStatement() },
                        Else = { Type = LogicalBlockType.None }
                    };
                    continuationCompletesLoopIteration = false;
                }
                else if (bodyIsLoopBreak)
                {
                    result = new IfLogic
                    {
                        ConditionBlock = root,
                        Condition = predicate.Expression,
                        PostDominator = continuationTarget,
                        Then =
                        {
                            Type = LogicalBlockType.Statement,
                            Statement = MoveLoopBreakArmToStatement(
                                bodyTarget, containingLoopBreak)
                        },
                        Else = { Type = LogicalBlockType.None }
                    };
                    if (bodyTarget != containingLoopBreak)
                    {
                        bodyTarget.Hidden = true;
                    }
                }
                else if (bodyIsLoopExitRegion)
                {
                    var initialBodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, containingLoopBreak, true);
                    StructureNestedConditionChains(initialBodyRegion, conditionBlocks);
                    foreach (var candidate in initialBodyRegion
                                 .Where(candidate => !conditionBlocks.Contains(candidate))
                                 .OrderByDescending(candidate => candidate.Start))
                    {
                        var nestedCondition = candidate.Statements.GetCondition();
                        if (nestedCondition != null &&
                            StructureIfElse(candidate, out var nestedLogic))
                        {
                            candidate.Statements.Replace(
                                nestedCondition, nestedLogic.Simplify().ToStatement());
                        }
                    }

                    var bodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, containingLoopBreak, true);
                    if (bodyRegion.Count == 0)
                    {
                        return false;
                    }

                    var body = new BlockStatement(bodyRegion, true);
                    body.Statements.Add(new BreakStatement());
                    bodyRegion.SafeHide();
                    result = new IfLogic
                    {
                        ConditionBlock = root,
                        Condition = predicate.Expression,
                        PostDominator = continuationTarget,
                        Then = { Type = LogicalBlockType.Statement, Statement = body },
                        Else = { Type = LogicalBlockType.None }
                    };
                }
                else
                {
                    var initialBodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, continuationTarget, true);
                    StructurePayloadLeafConditions(initialBodyRegion);
                    StructureNestedConditionChains(initialBodyRegion, conditionBlocks);
                    // 外层短路谓词只决定是否进入分支体；分支体内部仍可能包含
                    // 独立的 if/else。若直接收集基本块，这些条件会变成裸比较，
                    // 两侧副作用则被顺序输出。先从尾到头物化，再重新收集可见区域。
                    foreach (var candidate in initialBodyRegion
                                 .Where(candidate => !conditionBlocks.Contains(candidate))
                                 .OrderByDescending(candidate => candidate.Start))
                    {
                        var nestedCondition = candidate.Statements.GetCondition();
                        if (nestedCondition != null && StructureIfElse(candidate, out var nestedLogic))
                        {
                            candidate.Statements.Replace(
                                nestedCondition, nestedLogic.Simplify().ToStatement());
                        }
                    }

                    var bodyRegion = CollectDominatedBranchRegion(
                        root, bodyTarget, continuationTarget, true);
                    if (bodyRegion.Count == 0)
                    {
                        bodyRegion = new List<Block> { bodyTarget };
                    }
                    result = new IfLogic
                    {
                        ConditionBlock = root,
                        Condition = predicate.Expression,
                        PostDominator = continuationTarget,
                        Then = { Type = LogicalBlockType.BlockList, Blocks = bodyRegion },
                        Else = { Type = LogicalBlockType.None }
                    };
                }
            }

            HoistRepeatedInvocationComparisonTarget(root, conditionBlocks);
            foreach (var conditionBlock in conditionBlocks.Where(block => block != root))
            {
                conditionBlock.Hidden = true;
            }
            foreach (var passthroughBlock in passthroughBlocks)
            {
                passthroughBlock.Hidden = true;
            }

            logic = result;
            // 条件链的一端若直接回到循环头（或闩锁），这个目标表示“结束本轮”，
            // 并不是可供父级分支继续顺序执行的普通汇合点。把显式 continue 留在
            // 条件结构之后，可同时覆盖直接边和执行完正文后到达该目标的路径；
            // 即使条件恰位于循环体末尾，它也只是一条语义等价的冗余转移。
            if (continuationCompletesLoopIteration &&
                !HasOuterIterationCompletingOwner(
                    root, continuationTarget, containingLoop) &&
                !root.Statements.Any(statement => statement is ContinueStatement))
            {
                root.Statements.Add(new ContinueStatement());
            }
            return true;
        }

        /// <summary>
        /// 共享正文的布尔链中，一条条件路径可能不回到同级兄弟块，而是与兄弟块
        /// 分别直达同一个正文，例如 `(A &amp;&amp; B) || C` 的多个成功边。此时仍可
        /// 把该条件纳入同一谓词。但如果兄弟路径只能先经过当前块才到达后续正文，
        /// 当前块就是下一条 else-if（或分支体内嵌 if）的入口，不能仅因两者拥有
        /// 相同后支配点而把整棵分派树吞进第一条条件。
        /// </summary>
        private static bool SharePayloadArmWithoutCrossing(
            Block first, Block second, Block postDominator)
        {
            HashSet<Block> CollectPayloads(Block start, Block forbidden)
            {
                var payloads = new HashSet<Block>();
                var visited = new HashSet<Block>();
                var pending = new Stack<Block>();
                pending.Push(start);
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (current == null || current == forbidden ||
                        current == postDominator || !visited.Add(current))
                    {
                        continue;
                    }

                    if (!current.Statements.IsCondition() &&
                        !IsDecisionPassthrough(current))
                    {
                        payloads.Add(current);
                        continue;
                    }

                    foreach (var target in current.To)
                    {
                        pending.Push(target);
                    }
                }

                return payloads;
            }

            var firstPayloads = CollectPayloads(first, second);
            return firstPayloads.Count > 0 &&
                   firstPayloads.Overlaps(CollectPayloads(second, first));
        }

        /// <summary>
        /// merge If condition
        /// </summary>
        /// <param name="condition">current condition</param>
        /// <param name="conditionBlock">current condition block</param>
        /// <param name="dominator">if dominator</param>
        /// <param name="merge">merged if condition expression</param>
        /// <param name="then">assumed if true block</param>
        /// <param name="else">actual else block</param>
        /// <param name="isOrChain">true when called from an || merge path (not the initial call)</param>
        /// <returns></returns>
        private bool MergeIfCondition(ConditionExpression condition, Block conditionBlock, Block dominator, ref Expression merge,
            ref Block then, ref Block @else, bool isOrChain = false)
        {
            if (condition == null || _context?.BlockTable == null ||
                !_context.BlockTable.ContainsKey(condition.TrueBranch) ||
                !_context.BlockTable.ContainsKey(condition.FalseBranch))
            {
                return false;
            }

            if (dominator != null && condition.TrueBranch == dominator.Start)
            {
                condition = (ConditionExpression) condition.Invert();
            }

            then = _context.BlockTable[condition.TrueBranch]; //TODO: check me later
            var trueBlock = _context.BlockTable[condition.TrueBranch];
            var falseBlock = _context.BlockTable[condition.FalseBranch];
            var trueIsContent = IsBranchContent(trueBlock);
            var falseIsContent = IsBranchContent(falseBlock);

            // Allow else if merging by checking if falseBlock contains another condition
            if (trueBlock != then && falseBlock != dominator && !falseBlock.Statements.IsCondition()) //it's else if, can not merge
            {
                @else = conditionBlock;
                return false;
            }

            if (!trueIsContent && dominator == falseBlock)
            {
                var trueCondition = trueBlock.Statements.GetCondition();
                var savedThen = then;
                var savedElse = @else;
                if (MergeIfCondition(trueCondition, trueBlock, dominator, ref merge, ref then, ref @else))
                {
                    trueBlock.Hidden = true;
                    merge = condition.Condition.And(merge);
                    return true;
                }
                then = savedThen;
                @else = savedElse;
            }

            // && 合并路径补充：当 dominator 不等于 falseBlock（如 try 块内部），
            // 但 true 块的条件的 false 分支指向同一个 falseBlock 时，
            // 仍可构成 && 短路模式。
            if (!trueIsContent && dominator != falseBlock)
            {
                var trueCondition = trueBlock.Statements.GetCondition();
                if (trueCondition != null &&
                    _context.BlockTable.TryGetValue(trueCondition.FalseBranch, out var trueFalseTarget) &&
                    trueFalseTarget == falseBlock)
                {
                    var savedThen = then;
                    var savedElse = @else;
                    // 使用共享的 falseBlock 作为递归的 dominator
                    if (MergeIfCondition(trueCondition, trueBlock, falseBlock, ref merge, ref then, ref @else))
                    {
                        trueBlock.Hidden = true;
                        merge = condition.Condition.And(merge);
                        return true;
                    }
                    then = savedThen;
                    @else = savedElse;
                }
            }

            // || merge path: check if falseBlock continues the || chain.
            // Use direct check: falseBlock has a condition whose TrueBranch matches
            // the current trueBlock — this works even when IsBranchContent considers
            // falseBlock as "content" due to non-condition predecessors.
            {
                var falseCondition2 = falseBlock.Statements.GetCondition();
                bool isOrCandidate = falseCondition2 != null && then == trueBlock &&
                                     _context.BlockTable.ContainsKey(falseCondition2.TrueBranch) &&
                                     _context.BlockTable[falseCondition2.TrueBranch] == trueBlock;

                if (isOrCandidate)
                {
                    if (falseCondition2 == null)
                    {
                        falseCondition2 = falseBlock.Statements.GetCondition();
                    }

                    if (falseCondition2 != null)
                    {
                        var savedThen = then;
                        var savedElse = @else;
                        if (MergeIfCondition(falseCondition2, falseBlock, dominator, ref merge, ref then, ref @else, isOrChain: true))
                        {
                            falseBlock.Hidden = true;
                            merge = condition.Condition.Or(merge);
                            return true;
                        }
                        then = savedThen;
                        @else = savedElse;
                    }
                }
            }

            // Fallback: this is the final condition in a || chain.
            // When we're inside a recursive || merge and the falseBlock doesn't continue
            // the chain (different TrueBranch or not a condition block), this condition
            // is the last in the || chain, and falseBlock is the "else" code.
            if (isOrChain && then == trueBlock)
            {
                @else = falseBlock;
                merge = condition;
                return true;
            }

            if (trueBlock == then && falseBlock == dominator) //final condition
            {
                @else = dominator;
                merge = condition;
                return true;
            }

            @else = falseBlock;
            return false;
        }

        private bool IsBranchContent(Block block)
        {
            // 非分支块（单出口）→ 内容块
            if (block.To.Count < 2) return true;

            // 多个前驱且全为纯条件块 → 内容（如 A & (B || C) 模式，难以合并）
            if (block.From.Count > 1 &&
                block.From.All(b => b.Statements.IsCondition()))
                return true;

            // 纯条件块（仅含一个 ConditionExpression）→ 条件链节点，不视为内容
            if (block.Statements.IsCondition())
                return false;

            // 有非条件内容（条件之前或之后有其他语句）→ 内容块，不应被合并进条件链
            return true;
        }
    }
}
