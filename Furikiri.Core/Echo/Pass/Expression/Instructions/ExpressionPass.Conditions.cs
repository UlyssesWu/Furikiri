using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private void ProcessConditionInstruction(
            Block block,
            Instruction ins,
            Dictionary<int, Expression> ex,
            List<IAstNode> expList,
            ref Expression flag)
        {
            switch (ins.OpCode)
            {
                case OpCode.CEQ:
                case OpCode.CDEQ:
                case OpCode.CLT:
                case OpCode.CGT:
                {
                    //BUG: cdeq %-6, %0 left is null
                    var left = ex[ins.GetRegisterSlot(0)];
                    var right = ex[ins.GetRegisterSlot(1)];
                    BinaryOp op = BinaryOp.Unknown;
                    switch (ins.OpCode)
                    {
                        case OpCode.CEQ:
                            op = BinaryOp.Equal;
                            break;
                        case OpCode.CDEQ:
                            op = BinaryOp.Congruent;
                            break;
                        case OpCode.CLT:
                            op = BinaryOp.LessThan;
                            break;
                        case OpCode.CGT:
                            op = BinaryOp.GreaterThan;
                            break;
                    }

                    var b = new BinaryExpression(left, right, op);
                    flag = b;
                }
                    break;
                case OpCode.SETF:
                case OpCode.SETNF:
                {
                    var dst = ins.GetRegisterSlot(0);
                    switch (ins.OpCode)
                    {
                        case OpCode.SETF:
                            ex[dst] = flag;
                            break;
                        case OpCode.SETNF:
                            ex[dst] = flag.Invert();
                            break;
                    }
                    if (ShouldEmitStandaloneCall(block, ins, dst))
                    {
                        // 被丢弃的比较结果仍可能包含 getter；只保留其可观察求值，
                        // 不把已经死亡的比较标志误恢复成新的控制条件。
                        var effects = new List<Expression>();
                        ExpressionEffectAnalysis.CollectObservableEffects(ex[dst], effects);
                        foreach (var effect in effects) effect.RequiresStandaloneEvaluation = true;
                        expList.AddRange(effects.Select(effect => new ExpressionStatement(effect)));
                    }
                }
                    break;
                case OpCode.TT:
                {
                    flag = ex[ins.GetRegisterSlot(0)];
                }
                    break;
                case OpCode.TF:
                {
                    flag = ex[ins.GetRegisterSlot(0)].Invert();
                }
                    break;
                case OpCode.NF:
                {
                    if (flag is PhiExpression phi)
                    {
                        flag = phi.SimplifyBoolean();
                    }
                    flag = flag.Invert();
                }
                    break;
                case OpCode.JF:
                case OpCode.JNF:
                {
                    bool jmpFlag = ins.OpCode == OpCode.JF;
                    expList.Add(new ConditionExpression(flag, jmpFlag)
                        {JumpTo = ((JumpData) ins.Data).Goto.Line, ElseTo = ins.Line + 1});
                }
                    break;
                case OpCode.JMP:
                {
                    expList.Add(new GotoExpression {JumpTo = ((JumpData) ins.Data).Goto.Line});
                }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ins));
            }
        }
    }
}
