using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        public void BlockProcess(DecompileContext context, Block block,
            Dictionary<int, Expression> exps = null)
        {
            if (context.BlockFinalStates.ContainsKey(block) || block.Statements != null)
            {
                return;
            }

            //Process prev
            foreach (var prev in block.From)
            {
                if (prev != null && prev != block && !context.BlockFinalStates.ContainsKey(prev) && prev.Id <= block.Id) //TODO: just temp
                {
                    BlockProcess(context, prev);
                }
            }

            //merge final states from Froms
            if (exps == null)
            {
                var fromsExceptSelf = block.From.Where(b => b != block && context.BlockFinalStates.ContainsKey(b)).Distinct().ToList();
                if (fromsExceptSelf.Count > 1)
                {
                    var finalStates = new Dictionary<int, Expression>();
                    var allKeys = fromsExceptSelf.SelectMany(b => context.BlockFinalStates[b].Keys).Distinct();
                    foreach (var k in allKeys)
                    {
                        var expsList = fromsExceptSelf
                            .Select(b => context.BlockFinalStates[b].TryGetValue(k, out var v) ? v : null).ToList();
                        if (expsList.All(e => e == null))
                        {
                            continue;
                        }

                        if (expsList.All(e => e != null))
                        {
                            if (TryCollapseReenteredCollectionState(
                                    block, expsList, out var collectionState))
                            {
                                finalStates[k] = collectionState;
                                continue;
                            }

                            var first = expsList.First();
                            if (expsList.All(e => e.Equals(first)))
                            {
                                finalStates[k] = first;
                            }
                            else
                            {
                                var phi = new PhiExpression(k);
                                phi.PossibleExpressions.AddRange(expsList);
                                TryAnnotateConditionalPhi(block, fromsExceptSelf, phi);
                                // 在构造后再次用语义相等性检测：Equals 依赖引用相等，
                                // 但两个指向相同槽位的 LocalExpression 实例不是同一对象。
                                // CanSimplify 使用 AreSemanticallyEqual（按槽位/值比较）更为准确。
                                finalStates[k] = phi.CanSimplify ? phi.Simplify() : (Expression)phi;
                            }
                        }
                        else
                        {
                            var phi = new PhiExpression(k);
                            phi.PossibleExpressions.AddRange(expsList.Where(e => e != null));
                            finalStates[k] = phi;
                        }
                    }

                    exps = finalStates;
                }
                else if (fromsExceptSelf.Count == 1)
                {
                    exps = context.BlockFinalStates[fromsExceptSelf.First()];
                }
                else
                {
                    exps = new Dictionary<int, Expression>();
                }
            }

            // 从前驱递归恢复状态时，可能没有沿入口块传入两个特殊寄存器。
            // 它们是 VM 固有寄存器而非普通局部变量，任何基本块都应能直接引用。
            exps.TryAdd(Const.ThisReg, This);
            exps.TryAdd(Const.ThisProxyReg, ThisProxy);
            foreach (var parameter in context.Vars.Values.Where(variable => variable.IsParameter))
            {
                exps.TryAdd(parameter.Slot, new LocalExpression(parameter));
            }

            if (block.From.Count > 1)
            {
                var inputSets = block.From.Select(b => b.Def).Append(block.Input)
                    .Where(set => set != null)
                    .ToList();
                var commonInput = inputSets.Count > 0
                    ? inputSets.GetIntersection()
                    : new HashSet<int>();
                if (commonInput.Count > 0)
                {
                    foreach (var inSlot in commonInput)
                    {
                        if (inSlot == Const.FlagReg)
                        {
                            continue;
                        }
                        
                        // 已经从前驱块归并出结果时，不要被占位 Phi 覆盖
                        if (exps.ContainsKey(inSlot))
                        {
                            continue;
                        }

                        if (context.Vars.ContainsKey((short)inSlot))
                        {
                            exps[inSlot] = (LocalExpression)context.Vars[(short) inSlot];
                            continue;
                        }

                        // 对于仅由活跃输入推导出的槽位，优先使用局部引用占位，避免生成空 Phi
                        exps[inSlot] = GetLocalExpression(context, (short)inSlot);
                    }
                }

            }

            Expression retExp = null;
            var ex = new Dictionary<int, Expression>(exps);
            var flag = ex.TryGetValue(Const.FlagReg, out var fl) ? fl : null;

            var expList = new List<IAstNode>();
            block.Statements = expList;
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                ex[0] = Void;
                var ins = block.Instructions[i];

                // 某些合流块会在数据流分析尚未带入状态时直接读取寄存器。
                // 为所有显式寄存器准备稳定的局部引用；目标寄存器稍后会被对应指令覆盖。
                foreach (var register in ins.Registers.OfType<RegisterRef>())
                {
                    var slot = register.GetSlot();
                    if (slot != Const.ResourceReg && !ex.ContainsKey(slot))
                    {
                        ex[slot] = GetLocalExpression(context, slot);
                    }
                }

                if (SwapPatternRecognizer.TryMatch(block, i, out var swap))
                {
                    var left = BuildSwapOperand(context, ex, swap.LeftKind,
                        swap.LeftSlot, swap.LeftObjectSlot, swap.LeftMemberSlot,
                        swap.LeftMemberPath);
                    var right = BuildSwapOperand(context, ex, swap.RightKind,
                        swap.RightValueSlot, swap.ObjectSlot, swap.MemberSlot,
                        swap.MemberPath);

                    expList.Add(new BinaryExpression(left, right, BinaryOp.Swap));
                    if (swap.LeftKind == SwapOperandKind.Register)
                    {
                        ex[swap.LeftSlot] = left;
                    }
                    else
                    {
                        ex.Remove(swap.TemporarySlot);
                    }

                    if (swap.RightKind == SwapOperandKind.Register)
                    {
                        ex[swap.RightValueSlot] = right;
                    }
                    else if (swap.LeftKind == SwapOperandKind.Register)
                    {
                        // 局部与属性交换时，属性读取临时槽在交叉写回后不再有
                        // 源码级身份；保留左值仅供同一块的后续状态折叠。
                        ex[swap.RightValueSlot] = left;
                    }
                    else
                    {
                        ex.Remove(swap.RightValueSlot);
                    }
                    ex.Remove(swap.TemporarySlot);
                    i += swap.InstructionCount - 1;
                    continue;
                }

                ProcessInstruction(context, block, ins, ex, expList, ref flag, ref retExp, i);
            }

            // 同一寄存器值被连续写入多个目标时，恢复为链式赋值，确保调用、构造
            // 等右值只求值一次。
            CollapseChainedAssignments(expList);

            // 后处理：将赋值内联到后续调用的真实消费位置中。
            // 检测模式：expList[i] = BinaryExpression(Assign, left, right)，
            // 且 expList[j>i] 是 InvokeExpression，其接收者、方法表达式或参数与
            // right 是同一节点。此时用完整赋值替换该次消费，既恢复赋值表达式的
            // 返回值，也保证构造器、调用等有副作用的右值只执行一次。
            ExpressionValueIdentity.InlineAssignmentsIntoCallConsumers(expList);

            // with(call()) 会把同一次控制对象求值展开为多个相邻的成员读取。
            // 在表达式阶段重新引入局部缓存，避免 getter/函数被重复执行。
            HoistRepeatedMemberReceiverInvocations(context, expList);

            expList.RemoveAll(node => node is Expression exp && exp.Parent != null);

            //Save states
            ex[Const.FlagReg] = flag;
            context.BlockFinalStates[block] = new Dictionary<int, Expression>(ex);

            // 后继改由工作队列推进，避免大型对象的一条长顺序 CFG 直接形成同样深度的
            // C# 调用栈。汇合块若仍有较早的未处理前驱，方法开头的前驱恢复逻辑仍会
            // 先补齐该路径；队列中的重复节点随后会因已有最终状态而直接返回。
            foreach (var succ in block.To)
            {
                if (succ != null &&
                    !context.BlockFinalStates.ContainsKey(succ) &&
                    _queuedBlocks.Add(succ))
                {
                    _pendingBlocks.Enqueue(succ);
                }
            }

        }
    }
}
