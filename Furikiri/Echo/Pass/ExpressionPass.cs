using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    class ExpressionPass : IPass
    {
        public IdentifierExpression Global = new IdentifierExpression("global", IdentifierType.Global);
        public IdentifierExpression This = new IdentifierExpression("this", IdentifierType.This);
        public IdentifierExpression ThisProxy = new IdentifierExpression("", IdentifierType.ThisProxy);
        public ConstantExpression Void = new ConstantExpression(TjsVoid.Void);

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            var entry = context.EntryBlock;
            if (context.Object.ContextType == TjsContextType.TopLevel)
            {
                This.HideInstance = true;
                ThisProxy.HideInstance = true;
            }
            else
            {
                This.HideInstance = false;
                ThisProxy.HideInstance = false;
            }

            //Add global
            var exps = new Dictionary<int, Expression>
            {
                {Const.ThisReg, This},
                {Const.ThisProxyReg, ThisProxy},
            };
            //Add params
            var argCount = context.Object.FuncDeclArgCount;
            for (short i = 0; i < argCount; i++)
            {
                short slot = (short) (-i - 3);
                var v = new Variable(slot) {IsParameter = true};
                context.Vars.Add(slot, v);
                exps.Add(slot, new LocalExpression(v));
            }

            BlockProcess(context, entry, exps);

            //foreach (var variable in exps.Where(exp => exp.Value.Type == AstNodeType.LocalExpression).Select(exp =>
            //{
            //    var l = (LocalExpression)exp.Value;
            //    return new Variable(exp.Key) {VarType = l.DataType, IsParameter = l.IsParameter, Name = l.ToString()};
            //}))
            //{
            //    context.Vars.Add(variable.Slot, variable);
            //}

            return statement;
        }

        public void BlockProcess(DecompileContext context, Block block,
            Dictionary<int, Expression> exps)
        {
            if (block.Statements != null)
            {
                return;
            }

            if (block.From.Count > 1)
            {
                //get from.Output && from.Def
                var commonInput = block.From.Select(b => b.Output).Union(block.From.Select(b => b.Def)).GetIntersection();
                commonInput.IntersectWith(block.Input);
                //flag can be phi
                if (commonInput.Count > 0)
                {
                    foreach (var inSlot in commonInput)
                    {
                        //Generate Phi
                        var phi = new PhiExpression(inSlot);
                        //From must be sorted since we need first condition
                        if (block.From[0].Statements?.Last() is ConditionExpression condition)
                        {
                            phi.Condition = condition;
                            //var thenBlock = context.BlockTable[condition.JumpTo];
                            var elseBlock = context.BlockTable[condition.ElseTo];
                            //phi.ThenBranch = context.BlockFinalStates[trueBlock][inSlot];
                            phi.ThenBranch =
                                context.BlockFinalStates[block.From[0]]
                                    [inSlot]; //if jump, use the state from the jump-from block 
                            phi.ElseBranch = context.BlockFinalStates[elseBlock][inSlot];
                            //Next: Merge condition: if (v1) then v1 else v2 => v1 || v2 (infer v1 is bool)
                            if (phi.ThenBranch != phi.ElseBranch)
                            {
                                exps[inSlot] = phi;
                            }
                        }
                    }
                }
            }

            Expression retExp = null;
            var ex = new Dictionary<int, Expression>(exps);
            var flag = ex.ContainsKey(Const.FlagReg) ? ex[Const.FlagReg] : null;
            var expList = new List<IAstNode>();
            block.Statements = expList;
            InstructionData insData = null;
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                ex[0] = Void;
                var ins = block.Instructions[i];
                insData = block.InstructionDatas[i];

                switch (ins.OpCode)
                {
                    case OpCode.NOP:
                        break;
                    case OpCode.CONST:
                    {
                        var data = (OperandData) ins.Data;
                        var constExp = new ConstantExpression(data.Variant);
                        ex[ins.GetRegisterSlot(0)] = constExp;
                    }
                        break;
                    case OpCode.CL:
                    {
                        ex[ins.GetRegisterSlot(0)] = new ConstantExpression(TjsVoid.Void);
                    }
                        break;
                    case OpCode.CCL:
                        for (int j = ins.GetRegisterSlot(0); j < ins.GetRegisterSlot(1); j++)
                        {
                            ex[j] = new ConstantExpression(TjsVoid.Void);
                        }
                        break;
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
                    case OpCode.CHS:
                    case OpCode.INT:
                    case OpCode.REAL:
                    case OpCode.STR:
                    case OpCode.NUM:
                    case OpCode.OCTET:
                    case OpCode.LNOT:
                    case OpCode.INC:
                    case OpCode.DEC:
                    case OpCode.BNOT:
                    case OpCode.TYPEOF:
                    case OpCode.INV:
                    {
                        var dstSlot = ins.GetRegisterSlot(0);
                        var dst = ex[dstSlot];
                        var op = UnaryOp.Unknown;
                        switch (ins.OpCode)
                        {
                            case OpCode.INC:
                                op = UnaryOp.Inc;
                                break;
                            case OpCode.DEC:
                                op = UnaryOp.Dec;
                                break;
                            case OpCode.CHS:
                                op = UnaryOp.InvertSign;
                                break;
                            case OpCode.INT:
                                op = UnaryOp.ToInt;
                                break;
                            case OpCode.REAL:
                                op = UnaryOp.ToReal;
                                break;
                            case OpCode.STR:
                                op = UnaryOp.ToString;
                                break;
                            case OpCode.NUM:
                                op = UnaryOp.ToNumber;
                                break;
                            case OpCode.BNOT:
                                op = UnaryOp.BitNot;
                                break;
                            case OpCode.OCTET:
                                op = UnaryOp.ToByteArray;
                                break;
                            case OpCode.LNOT:
                                op = UnaryOp.Not;
                                break;
                            case OpCode.TYPEOF:
                                op = UnaryOp.TypeOf;
                                break;
                            case OpCode.INV:
                                op = UnaryOp.Invalidate;
                                break;
                        }

                        var u = new UnaryExpression(dst, op);
                        //ex[dstSlot] = u;
                        expList.Add(u);
                    }
                        break;
                    case OpCode.INCPD:
                    case OpCode.DECPD:
                    case OpCode.TYPEOFD:
                    {
                        var res = ins.GetRegisterSlot(0);
                        var obj = ins.GetRegisterSlot(1);
                        var name = ins.Data.AsString();
                        var op = UnaryOp.Unknown;
                        switch (ins.OpCode)
                        {
                            case OpCode.INCPI:
                                op = UnaryOp.Inc;
                                break;
                            case OpCode.DECPI:
                                op = UnaryOp.Dec;
                                break;
                            case OpCode.TYPEOFD:
                                op = UnaryOp.TypeOf;
                                break;
                        }

                        //var u = new UnaryExpression(new IdentifierExpression(name), op) {Instance = ex[obj]};
                        var u = new UnaryExpression(new IdentifierExpression(name) {Instance = ex[obj]}, op);
                        if (res != 0) //copy to %res
                        {
                            ex[res] = u;
                        }

                        expList.Add(u);
                    }
                        break;
                    case OpCode.INCPI:
                    case OpCode.DECPI:
                    case OpCode.TYPEOFI:
                    {
                        var res = ins.GetRegisterSlot(0);
                        var obj = ins.GetRegisterSlot(1);
                        var name = ins.GetRegisterSlot(2);
                        var op = UnaryOp.Unknown;
                        switch (ins.OpCode)
                        {
                            case OpCode.INCPI:
                                op = UnaryOp.Inc;
                                break;
                            case OpCode.DECPI:
                                op = UnaryOp.Dec;
                                break;
                            case OpCode.TYPEOFI:
                                op = UnaryOp.TypeOf;
                                break;
                        }

                        var u = new UnaryExpression(new PropertyAccessExpression(ex[name], ex[obj]), op);
                        if (res != 0) //copy to %res
                        {
                            ex[res] = u;
                        }

                        expList.Add(u);
                    }
                        break;
                    case OpCode.INCP:
                    case OpCode.DECP:
                        break;
                    case OpCode.LORP:
                        break;
                    case OpCode.LANDP:
                        break;
                    case OpCode.BORP:
                        break;
                    case OpCode.BXORP:
                        break;
                    case OpCode.BANDP:
                        break;
                    case OpCode.SARP:
                        break;
                    case OpCode.SALP:
                        break;
                    case OpCode.SRP:
                        break;
                    case OpCode.CP:
                    {
                        var dstSlot = ins.GetRegisterSlot(0);
                        var srcSlot = ins.GetRegisterSlot(1);

                        Expression src;
                        if (ex.ContainsKey(srcSlot))
                        {
                            src = ex[srcSlot];
                        }
                        else
                        {
                            src = new LocalExpression(context.Object, srcSlot);
                        }

                        Expression dst = null;
                        if (ex.ContainsKey(dstSlot))
                        {
                            //dst = ex[dstSlot];
                            ex[dstSlot] = src;
                        }
                        else if (dstSlot < -2)
                        {
                            var l = new LocalExpression(context.Object, dstSlot);
                            //if (!l.IsParameter)
                            //{
                            //    expList.Add(l);
                            //}
                            dst = l;
                            ex[dstSlot] = l; //assignment -> statements, local -> expressions

                            BinaryExpression b = new BinaryExpression(dst, src, BinaryOp.Assign) {IsDeclaration = true};
                            //ex[dstSlot] = b;
                            expList.Add(b);
                        }
                        else if (dstSlot != 0)
                        {
                            ex[dstSlot] = src;
                        }
                    }
                        break;
                    //Binary Operation
                    case OpCode.ADD:
                    case OpCode.SUB:
                    case OpCode.MOD:
                    case OpCode.DIV:
                    case OpCode.IDIV:
                    case OpCode.MUL:
                    case OpCode.BAND:
                    case OpCode.BOR:
                    case OpCode.BXOR:
                    case OpCode.LAND:
                    case OpCode.LOR:
                    case OpCode.SAR:
                    case OpCode.SAL:
                    case OpCode.SR:
                    case OpCode.CHKINS:
                    {
                        var dstSlot = ins.GetRegisterSlot(0);
                        var srcSlot = ins.GetRegisterSlot(1);
                        var store = false; //Set to Expression
                        var declare = false; //Is declaration

                        Expression dst = null;
                        if (ex.ContainsKey(dstSlot))
                        {
                            dst = ex[dstSlot];
                        }
                        else if (dstSlot < -2)
                        {
                            var l = new LocalExpression(context.Object, dstSlot);
                            //if (!l.IsParameter)
                            //{
                            //    expList.Add(l);
                            //}
                            dst = l;
                            ex[dstSlot] = l;
                            store = false;
                            declare = true;
                        }

                        Expression src;
                        if (ex.ContainsKey(srcSlot))
                        {
                            src = ex[srcSlot];
                        }
                        else
                        {
                            src = new LocalExpression(context.Object, srcSlot);
                        }

                        var op = BinaryOp.Unknown;
                        switch (ins.OpCode)
                        {
                            case OpCode.ADD:
                                op = BinaryOp.Add;
                                break;
                            case OpCode.SUB:
                                op = BinaryOp.Sub;
                                break;
                            case OpCode.MOD:
                                op = BinaryOp.Mod;
                                break;
                            case OpCode.DIV:
                                op = BinaryOp.Div;
                                break;
                            case OpCode.IDIV:
                                op = BinaryOp.Idiv;
                                break;
                            case OpCode.MUL:
                                op = BinaryOp.Mul;
                                break;
                            case OpCode.BAND:
                                op = BinaryOp.BitAnd;
                                break;
                            case OpCode.BOR:
                                op = BinaryOp.BitOr;
                                break;
                            case OpCode.BXOR:
                                op = BinaryOp.BitXor;
                                break;
                            case OpCode.LAND:
                                op = BinaryOp.LogicAnd;
                                break;
                            case OpCode.LOR:
                                op = BinaryOp.LogicOr;
                                break;
                            case OpCode.SAR:
                                op = BinaryOp.NumberShiftRight;
                                break;
                            case OpCode.SAL:
                                op = BinaryOp.NumberShiftLeft;
                                break;
                            case OpCode.SR:
                                op = BinaryOp.BitShiftRight;
                                break;
                            //case OpCode.CP: //moved!
                            //    op = BinaryOp.Assign;
                            //    push = true;
                            //break;
                            case OpCode.CHKINS:
                                op = BinaryOp.InstanceOf;
                                break;
                        }

                        BinaryExpression b = new BinaryExpression(dst, src, op) {IsDeclaration = declare};

                        if (store)
                        {
                            ex[dstSlot] = b;
                        }

                        expList.Add(b);
                    }
                        break;
                    case OpCode.ADDPD:
                    case OpCode.SUBPD:
                    case OpCode.MODPD:
                    case OpCode.DIVPD:
                    case OpCode.IDIVPD:
                    case OpCode.MULPD:
                    case OpCode.BANDPD:
                    case OpCode.BORPD:
                    case OpCode.BXORPD:
                    case OpCode.LANDPD:
                    case OpCode.LORPD:
                    case OpCode.SARPD:
                    case OpCode.SALPD:
                    case OpCode.SRPD:
                    {
                        var res = ins.GetRegisterSlot(0);
                        var obj = ins.GetRegisterSlot(1);
                        var name = ins.Data.AsString();
                        var op = BinaryOp.Unknown;

                        var src = ex[ins.GetRegisterSlot(3)];
                        switch (ins.OpCode)
                        {
                            case OpCode.ADDPD:
                                op = BinaryOp.Add;
                                break;
                            case OpCode.SUBPD:
                                op = BinaryOp.Sub;
                                break;
                            case OpCode.MODPD:
                                op = BinaryOp.Mod;
                                break;
                            case OpCode.DIVPD:
                                op = BinaryOp.Div;
                                break;
                            case OpCode.IDIVPD:
                                op = BinaryOp.Idiv;
                                break;
                            case OpCode.MULPD:
                                op = BinaryOp.Mul;
                                break;
                            case OpCode.BANDPD:
                                op = BinaryOp.BitAnd;
                                break;
                            case OpCode.BORPD:
                                op = BinaryOp.BitOr;
                                break;
                            case OpCode.BXORPD:
                                op = BinaryOp.BitXor;
                                break;
                            case OpCode.LANDPD:
                                op = BinaryOp.LogicAnd;
                                break;
                            case OpCode.LORPD:
                                op = BinaryOp.LogicOr;
                                break;
                            case OpCode.SARPD:
                                op = BinaryOp.NumberShiftRight;
                                break;
                            case OpCode.SALPD:
                                op = BinaryOp.NumberShiftLeft;
                                break;
                            case OpCode.SRPD:
                                op = BinaryOp.BitShiftRight;
                                break;
                        }

                        BinaryExpression b = new BinaryExpression(new IdentifierExpression(name) {Instance = ex[obj]},
                            src, op);

                        if (res != 0)
                        {
                            ex[res] = b;
                        }

                        expList.Add(b);
                    }
                        break;
                    case OpCode.ADDPI:
                    case OpCode.SUBPI:
                    case OpCode.MODPI:
                    case OpCode.DIVPI:
                    case OpCode.IDIVPI:
                    case OpCode.MULPI:
                    case OpCode.BANDPI:
                    case OpCode.BORPI:
                    case OpCode.BXORPI:
                    case OpCode.LANDPI:
                    case OpCode.LORPI:
                    case OpCode.SARPI:
                    case OpCode.SALPI:
                    case OpCode.SRPI:
                    {
                        var res = ins.GetRegisterSlot(0);
                        var obj = ins.GetRegisterSlot(1);
                        var name = ins.GetRegisterSlot(2);
                        var op = BinaryOp.Unknown;

                        var src = ex[ins.GetRegisterSlot(3)];
                        switch (ins.OpCode)
                        {
                            case OpCode.ADDPI:
                                op = BinaryOp.Add;
                                break;
                            case OpCode.SUBPI:
                                op = BinaryOp.Sub;
                                break;
                            case OpCode.MODPI:
                                op = BinaryOp.Mod;
                                break;
                            case OpCode.DIVPI:
                                op = BinaryOp.Div;
                                break;
                            case OpCode.IDIVPI:
                                op = BinaryOp.Idiv;
                                break;
                            case OpCode.MULPI:
                                op = BinaryOp.Mul;
                                break;
                            case OpCode.BANDPI:
                                op = BinaryOp.BitAnd;
                                break;
                            case OpCode.BORPI:
                                op = BinaryOp.BitOr;
                                break;
                            case OpCode.BXORPI:
                                op = BinaryOp.BitXor;
                                break;
                            case OpCode.LANDPI:
                                op = BinaryOp.LogicAnd;
                                break;
                            case OpCode.LORPI:
                                op = BinaryOp.LogicOr;
                                break;
                            case OpCode.SARPI:
                                op = BinaryOp.NumberShiftRight;
                                break;
                            case OpCode.SALPI:
                                op = BinaryOp.NumberShiftLeft;
                                break;
                            case OpCode.SRPI:
                                op = BinaryOp.BitShiftRight;
                                break;
                        }

                        BinaryExpression b =
                            new BinaryExpression(new PropertyAccessExpression(ex[name], ex[obj]), src, op);

                        if (res != 0)
                        {
                            ex[res] = b;
                        }

                        expList.Add(b);
                    }
                        break;
                    case OpCode.ADDP:
                        break;
                    case OpCode.SUBP:
                        break;
                    case OpCode.MODP:
                        break;
                    case OpCode.DIVP:
                        break;
                    case OpCode.IDIVP:
                        break;
                    case OpCode.MULP:
                        break;
                    case OpCode.EVAL:
                        break;
                    case OpCode.EEXP:
                        break;
                    case OpCode.ASC:
                        break;
                    case OpCode.CHR:
                        break;
                    case OpCode.CHKINV:
                        break;
                    //Invoke
                    case OpCode.CALL:
                    {
                        var method = ex[ins.GetRegisterSlot(1)];
                        var call = new InvokeExpression(method);
                        var dst = ins.GetRegisterSlot(0);
                        call.Instance = null;
                        var paramCount = ins.GetRegisterSlot(2);
                        if (paramCount == -1)
                        {
                            //...
                            //do nothing
                        }
                        else
                        {
                            for (int j = 0; j < paramCount; j++)
                            {
                                var pSlot = ins.GetRegisterSlot(3 + j);
                                call.Parameters.Add(ex[pSlot]);
                            }
                        }

                        ex[dst] = call;
                        //if (dst == 0) //just execute and discard result
                        //{
                        //    expList.Add(call);
                        //}
                        expList.Add(call);
                    }
                        break;
                    case OpCode.CALLD:
                    {
                        var callMethodName = ins.Data.AsString();
                        var call = new InvokeExpression(callMethodName);
                        var dst = ins.GetRegisterSlot(0);
                        var callerSlot = ins.GetRegisterSlot(1);
                        call.Instance = ex[callerSlot];
                        var paramCount = ins.GetRegisterSlot(3);
                        if (paramCount == -1)
                        {
                            //...
                            //do nothing
                        }
                        else
                        {
                            for (int j = 0; j < paramCount; j++)
                            {
                                var pSlot = ins.GetRegisterSlot(4 + j);
                                ex[pSlot].Parent = call;
                                call.Parameters.Add(ex[pSlot]);
                            }
                        }

                        ex[dst] = call;
                        if (dst == 0) //just execute and discard result
                        {
                            //Handle RegExp()._compile("//g/[^A-Za-z]")
                            if (callMethodName == Const.RegExpCompile)
                            {
                                if (call.Instance is InvokeExpression invoke && invoke.Method == Const.RegExp)
                                {
                                    call.InvokeType = InvokeType.RegExpCompile;
                                    ex[callerSlot] = call;
                                    break;
                                }
                            }

                            expList.Add(call);
                        }
                    }
                        break;
                    case OpCode.CALLI:
                    {
                        //InvokeExpression call = null;
                        //var operand = ((OperandData) ins.Data).Variant;
                        //if (operand is TjsString str)
                        //{
                        //    call = new InvokeExpression(str.StringValue);
                        //}
                        //else
                        //{
                        //    call = new InvokeExpression(operand as TjsCodeObject);
                        //}
                        InvokeExpression call = new InvokeExpression(ex[ins.GetRegisterSlot(2)]);
                        var dst = ins.GetRegisterSlot(0);
                        var callerSlot = ins.GetRegisterSlot(1);
                        call.Instance = ex[callerSlot];
                        var paramCount = ins.GetRegisterSlot(3);
                        if (paramCount == -1)
                        {
                            //...
                            //do nothing
                        }
                        else
                        {
                            for (int j = 0; j < paramCount; j++)
                            {
                                var pSlot = ins.GetRegisterSlot(4 + j);
                                ex[pSlot].Parent = call;
                                call.Parameters.Add(ex[pSlot]);
                            }
                        }

                        ex[dst] = call;
                        //if (dst == 0) //just execute and discard result
                        //{
                        //    expList.Add(call);
                        //}
                        expList.Add(call);
                    }
                        break;
                    case OpCode.NEW:
                    {
                        InvokeExpression call = new InvokeExpression(ex[ins.GetRegisterSlot(1)]) {InvokeType = InvokeType.Ctor};
                        var dst = ins.GetRegisterSlot(0);
                        call.Instance = null;
                        var paramCount = ins.GetRegisterSlot(2);
                        if (paramCount == -1)
                        {
                            //...
                            //do nothing
                        }
                        else
                        {
                            for (int j = 0; j < paramCount; j++)
                            {
                                var pSlot = ins.GetRegisterSlot(3 + j);
                                ex[pSlot].Parent = call;
                                call.Parameters.Add(ex[pSlot]);
                            }
                        }

                        ex[dst] = call;
                        //if (dst == 0) //just execute and discard result
                        //{
                        //    expList.Add(call);
                        //}
                        expList.Add(call);
                    }
                        break;
                    case OpCode.GPD:
                    case OpCode.GPDS:
                    {
                        var dst = ins.GetRegisterSlot(0);
                        var slot = ins.GetRegisterSlot(1);
                        var instance = ex[slot];
                        var name = ins.Data.AsString();
                        var newId = new IdentifierExpression(name) {Instance = instance};
                        ex[dst] = newId;
                    }
                        break;
                    case OpCode.GPI:
                    case OpCode.GPIS:
                    {
                        var dst = ins.GetRegisterSlot(0);
                        var obj = ins.GetRegisterSlot(1);
                        var name = ins.GetRegisterSlot(2);

                        PropertyAccessExpression p = new PropertyAccessExpression(ex[name], ex[obj]);
                        ex[dst] = p;
                    }
                        break;
                    case OpCode.SPI:
                    case OpCode.SPIE:
                    case OpCode.SPIS:
                    {
                        var obj = ins.GetRegisterSlot(0);
                        var name = ins.GetRegisterSlot(1);
                        var src = ins.GetRegisterSlot(2);

                        BinaryExpression b = new BinaryExpression(new PropertyAccessExpression(ex[name], ex[obj]),
                            ex[src], BinaryOp.Assign);
                        expList.Add(b); //there is no other way to find this expression
                    }
                        break;
                    //Set
                    case OpCode.SPD:
                    case OpCode.SPDE:
                    case OpCode.SPDEH:
                    case OpCode.SPDS:
                    {
                        var left = new IdentifierExpression(ins.Data.AsString())
                            {Instance = ex[ins.GetRegisterSlot(0)]};
                        var right = ex[ins.GetRegisterSlot(2)];
                        BinaryExpression b = new BinaryExpression(left, right, BinaryOp.Assign);
                        //check declare
                        if (context.Object.ContextType == TjsContextType.TopLevel)
                        {
                            if (!context.RegisteredMembers.ContainsKey(left.Name))
                            {
                                b.IsDeclaration = true;
                                var stub = new TjsStub();
                                if (right is ConstantExpression con) //TODO: better type check
                                {
                                    stub.Type = con.DataType;
                                }

                                context.RegisteredMembers[left.Name] = stub;
                            }
                        }

                        expList.Add(b);
                    }
                        break;
                    case OpCode.SETP:
                    {
                    }
                        break;
                    case OpCode.GETP:
                    {
                    }
                        break;
                    //Delete
                    case OpCode.DELD:
                        DeleteExpression d = new DeleteExpression(ins.Data.AsString());
                        d.Instance = ex[ins.GetRegisterSlot(1)];
                        expList.Add(d);
                        break;
                    case OpCode.DELI:
                        DeleteExpression d2 = new DeleteExpression(ex[ins.GetRegisterSlot(2)]);
                        d2.Instance = ex[ins.GetRegisterSlot(1)];
                        //Check declare
                        if (d2.Instance is IdentifierExpression toDel)
                        {
                            if (context.RegisteredMembers.ContainsKey(toDel.Name))
                            {
                                context.RegisteredMembers.Remove(toDel.Name);
                            }
                        }

                        expList.Add(d2);
                        break;
                    case OpCode.SRV:
                    {
                        var srv = ins.GetRegisterSlot(0);
                        retExp = srv == 0 ? null : ex[srv];
                    }
                        break;
                    case OpCode.RET:
                    {
                        expList.Add(new ReturnExpression(retExp));
                    }
                        break;
                    case OpCode.ENTRY:
                        break;
                    case OpCode.EXTRY:
                        break;
                    case OpCode.THROW:
                    {
                        var th = new ThrowExpression(ex[ins.GetRegisterSlot(0)]);
                        expList.Add(th);
                    }
                        break;
                    case OpCode.CHGTHIS:
                    {
                        var ico = new BinaryExpression(ex[ins.GetRegisterSlot(0)], ex[ins.GetRegisterSlot(1)], BinaryOp.InContextOf);
                        ex[ins.GetRegisterSlot(0)] = ico;
                    }
                        break;
                    case OpCode.GLOBAL:
                    {
                        ex[ins.GetRegisterSlot(0)] = Global;
                    }
                        break;
                    case OpCode.ADDCI:
                        break;
                    case OpCode.REGMEMBER:
                        break;
                    case OpCode.DEBUGGER:
                        break;
                    case OpCode.LAST:
                        break;
                    case OpCode.PreDec:
                        break;
                    case OpCode.PostInc:
                        break;
                    case OpCode.PostDec:
                        break;
                    case OpCode.Delete:
                        break;
                    case OpCode.FuncCall:
                        break;
                    case OpCode.IgnorePropGet:
                        break;
                    case OpCode.IgnorePropSet:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            expList.RemoveAll(node => node is Expression exp && exp.Parent != null);

            //Save states
            ex[Const.FlagReg] = flag;
            context.BlockFinalStates[block] = ex;

            //Process next
            foreach (var succ in block.To)
            {
                BlockProcess(context, succ, new Dictionary<int, Expression>(ex)); //TODO: validate if deep copy ex is correct
            }
        }
    }
}