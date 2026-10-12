using System;
using System.Collections.Generic;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private void ProcessTransferInstruction(
            Instruction ins,
            Dictionary<int, Expression> ex,
            List<IAstNode> expList,
            ref Expression retExp)
        {
            switch (ins.OpCode)
            {
                case OpCode.SRV:
                {
                    var srv = ins.GetRegisterSlot(0);
                    if (srv == 0)
                    {
                        retExp = null;
                    }
                    else
                    {
                        // Some scripts can carry SRV with a register that is not materialized
                        // in the current block state (e.g. path-dependent temp). Treat as void
                        // instead of crashing decompilation.
                        retExp = ex.TryGetValue(srv, out var resolved) ? resolved : null;
                    }
                }
                    break;
                case OpCode.RET:
                {
                    expList.Add(new ReturnExpression(retExp));
                }
                    break;
                case OpCode.ENTRY:
                    var catchVar = new IdentifierExpression(Const.DefaultCatchVarName);
                    // ENTRY layout: entry <jump-offset>, <catch-register>
                    ex[ins.GetRegisterSlot(1)] = catchVar;
                    expList.Add(new CatchExpression(catchVar));
                    break;
                case OpCode.EXTRY:
                    //extry + jmp = 无异常退出
                    break;
                case OpCode.THROW:
                {
                    var th = new ThrowExpression(ex[ins.GetRegisterSlot(0)]);
                    expList.Add(th);
                }
                    break;
                case OpCode.DEBUGGER:
                    expList.Add(new DebuggerStatement());
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(ins));
            }
        }
    }
}
