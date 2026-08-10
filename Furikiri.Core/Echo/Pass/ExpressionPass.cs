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
                short slot = (short) (-i + Const.ArgBase);
                var v = new Variable(slot) {IsParameter = true};
                context.Vars.Add(slot, v);
                exps.Add(slot, new LocalExpression(v));
            }

            // create array params if needed
            var hasUnnamedArray = context.Object.FuncDeclUnnamedArgArrayBase > 0; //not sure
            var hasCollapseArray = context.Object.FuncDeclCollapseBase >= 0;
            if (hasCollapseArray || hasUnnamedArray)
            {
                short slot = (short) ((hasCollapseArray? - context.Object.FuncDeclCollapseBase : - context.Object.FuncDeclUnnamedArgArrayBase) + Const.ArgBase);
                var array = new Variable(slot) { IsParameter = true, VarType = TjsVarType.Object};
                if (hasCollapseArray)
                {
                    array.IsNamedArray = true;
                }
                else
                {
                    array.IsUnnamedArray = true;
                }
                context.Vars.Add(slot, array);
                exps.Add(slot, new LocalExpression(array));
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

        private static bool IsCollectionCtor(Expression expression)
        {
            if (expression is not InvokeExpression invoke || invoke.InvokeType != InvokeType.Ctor)
            {
                return false;
            }

            if (invoke.MethodExpression is IdentifierExpression id)
            {
                return id.FullName == "global.Array" || id.FullName == "global.Dictionary";
            }

            return false;
        }

        private static string GetUniqueDerivedLocalName(
            string baseName,
            DecompileContext context,
            Dictionary<int, Expression> ex,
            List<IAstNode> expList)
        {
            if (string.IsNullOrEmpty(baseName))
            {
                return baseName;
            }

            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var variable in context.Vars.Values)
            {
                if (!string.IsNullOrEmpty(variable.Name))
                {
                    usedNames.Add(variable.Name);
                }
            }

            foreach (var local in ex.Values.OfType<LocalExpression>())
            {
                if (!string.IsNullOrEmpty(local.VariableDef.Name))
                {
                    usedNames.Add(local.VariableDef.Name);
                }
            }

            foreach (var assign in expList.OfType<BinaryExpression>())
            {
                if (assign.Left is LocalExpression local && !string.IsNullOrEmpty(local.VariableDef.Name))
                {
                    usedNames.Add(local.VariableDef.Name);
                }
            }

            if (!usedNames.Contains(baseName))
            {
                return baseName;
            }

            for (var suffix = 2;; suffix++)
            {
                var candidate = $"{baseName}{suffix}";
                if (!usedNames.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        public void BlockProcess(DecompileContext context, Block block,
            Dictionary<int, Expression> exps = null)
        {
            if (context.BlockFinalStates.ContainsKey(block) || block.Statements != null)
            {
                return;
            }

            //context.ProcessVisitedBlocks.Add(block.Id);

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
                var fromsExceptSelf = block.From.Where(b => b != block && context.BlockFinalStates.ContainsKey(b)).ToList();
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

            if (block.From.Count > 1)
            {
                var commonInput = block.From.Select(b => b.Def).Append(block.Input).GetIntersection();
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
                        exps[inSlot] = new LocalExpression(context.Object, (short)inSlot);
                    }
                }
                ////get from.Output && from.Def
                //var commonInput = block.From.Select(b => b.Output).Union(block.From.Select(b => b.Def)).GetIntersection();
                //commonInput.IntersectWith(block.Input);
                ////flag can be phi
                //if (commonInput.Count > 0)
                //{
                //    foreach (var inSlot in commonInput)
                //    {
                //        //Generate Phi
                //        var phi = new PhiExpression(inSlot);
                //        //From must be sorted since we need first condition
                //        //TODO: don't even have statements here.
                //        if (block.From[0].Statements?.LastOrDefault() is ConditionExpression condition)
                //        {
                //            phi.Condition = condition;
                //            //var thenBlock = context.BlockTable[condition.JumpTo];
                //            var elseBlock = context.BlockTable[condition.ElseTo];
                //            //phi.ThenBranch = context.BlockFinalStates[trueBlock][inSlot];
                //            phi.ThenBranch =
                //                context.BlockFinalStates[block.From[0]]
                //                    [inSlot]; //if jump, use the state from the jump-from block 
                //            phi.ElseBranch = context.BlockFinalStates[elseBlock][inSlot];
                //            //Next: Merge condition: if (v1) then v1 else v2 => v1 || v2 (infer v1 is bool)
                //            if (phi.ThenBranch != phi.ElseBranch)
                //            {
                //                exps[inSlot] = phi;
                //            }
                //        }
                //    }
                //}
            }

            Expression retExp = null;
            var ex = new Dictionary<int, Expression>(exps);
            var flag = ex.TryGetValue(Const.FlagReg, out var fl) ? fl : null;

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

                        // INC/DEC on VM temp registers often model numeric index stepping.
                        // If the target is a concrete integer constant, fold it directly to
                        // avoid producing invalid chains like 0++++ in decompiled output.
                        if ((op == UnaryOp.Inc || op == UnaryOp.Dec) &&
                            dst is ConstantExpression constExpr &&
                            constExpr.Variant is TjsInt intVal)
                        {
                            var nextValue = op == UnaryOp.Inc ? intVal.IntValue + 1 : intVal.IntValue - 1;
                            var folded = new ConstantExpression(new TjsInt(nextValue));
                            ex[dstSlot] = folded;
                            break;
                        }

                        var u = new UnaryExpression(dst, op);
                        if (dstSlot > 0)
                        {
                            ex[dstSlot] = u;
                        }
                        // INV (invalidate) 是必须作为语句输出的副作用操作，
                        // 无论目标寄存器是参数槽还是临时寄存器，都应加入 expList。
                        if (dstSlot <= Const.ArgBase || op == UnaryOp.Invalidate)
                        {
                            expList.Add(u);
                        }
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
                            case OpCode.INCPD:
                                op = UnaryOp.Inc;
                                break;
                            case OpCode.DECPD:
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
                            u.IsPrefix = true; // INCPD/DECPD 存储操作后的新值，语义上为前置运算符
                            ex[res] = u;
                        }

                        // 只有当结果被丢弃（res==0）时才作为独立语句输出；
                        // 若 res!=0，结果将被下游指令（如 CEQ）内联使用，
                        // 不应再作为单独语句输出以避免重复执行。
                        if (res == 0)
                        {
                            expList.Add(u);
                        }
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
                            u.IsPrefix = true; // INCPI/DECPI 存储操作后的新值，语义上为前置运算符
                            ex[res] = u;
                        }

                        if (res == 0)
                        {
                            expList.Add(u);
                        }
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
                        if (dstSlot <= Const.ArgBase)
                        {
                            var declare = !context.IsArg(dstSlot);
                            var l = new LocalExpression(context.Object, dstSlot);

                            //This is wrong. todo: phi
                            //if ((src is LocalExpression localExp && localExp.VariableDef.Slot == dstSlot) || (src is BinaryExpression binaryExp && binaryExp.Left is LocalExpression le && le.Slot == dstSlot))
                            //{
                            //    ex[dstSlot] = l;
                            //    continue;
                            //}

                            //if (!l.IsParameter)
                            //{
                            //    expList.Add(l);
                            //}
                            dst = l;
                            ex[dstSlot] = l; //assignment -> statements, local -> expressions

                            // 当局部变量从已知属性/方法赋值时，使用属性名+'_'命名
                            if (declare && l.VariableDef.Name == null && src is IdentifierExpression srcId && srcId.Instance != null && !string.IsNullOrEmpty(srcId.Name))
                            {
                                var baseName = srcId.Name + "_";
                                l.VariableDef.Name = GetUniqueDerivedLocalName(baseName, context, ex, expList);
                            }

                            BinaryExpression b = new BinaryExpression(dst, src, BinaryOp.Assign) {IsDeclaration = declare };
                            var insertedDeclarationAtArrayInit = false;
                            // Keep temp collection constructor aliases stable so subsequent
                            // element assignments print against the local variable rather than
                            // repeatedly materializing anonymous literals (e.g. [][0] = ...).
                            if (srcSlot > Const.ArgBase && IsCollectionCtor(src))
                            {
                                ex[srcSlot] = l;
                                var firstAssignIndex = -1;
                                for (var k = 0; k < expList.Count; k++)
                                {
                                    if (expList[k] is BinaryExpression prevAssign &&
                                        prevAssign.Left is PropertyAccessExpression access &&
                                        ReferenceEquals(access.Instance, src))
                                    {
                                        if (firstAssignIndex < 0)
                                        {
                                            firstAssignIndex = k;
                                        }
                                        access.Instance = l;
                                    }
                                }

                                if (firstAssignIndex >= 0)
                                {
                                    expList.Insert(firstAssignIndex, b);
                                    insertedDeclarationAtArrayInit = true;
                                }
                            }
                            else if (srcSlot > Const.ArgBase)
                            {
                                // 当临时寄存器被赋给命名局部变量后，将该临时寄存器重定向到局部变量。
                                // 这样后续指令（如 CDEQ）对同一临时寄存器的引用会变成对命名局部变量的引用，
                                // 而不是再次展开原始表达式（如重复函数调用）。
                                ex[srcSlot] = l;
                            }

                            if (!insertedDeclarationAtArrayInit)
                            {
                                //ex[dstSlot] = b;
                                expList.Add(b);
                            }
                        }
                        else if (ex.ContainsKey(dstSlot))
                        {
                            //dst = ex[dstSlot];
                            ex[dstSlot] = src;
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
                        var saveToEx = true; //Set to Expression
                        var appendToExpList = dstSlot <= Const.ArgBase;
                        var declare = false; //Is declaration

                        Expression dst = null;
                        if (ex.ContainsKey(dstSlot))
                        {
                            dst = ex[dstSlot];
                        }
                        else if (dstSlot <= Const.ArgBase)
                        {
                            var l = new LocalExpression(context.Object, dstSlot);
                            //if (!l.IsParameter)
                            //{
                            //    expList.Add(l);
                            //}
                            dst = l;
                            ex[dstSlot] = l;
                            saveToEx = false;
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

                        if (saveToEx)
                        {
                            ex[dstSlot] = b;
                        }

                        if (appendToExpList)
                        {
                            expList.Add(b); //TODO: need add?
                        }
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
                    {
                        var srcSlot = ins.GetRegisterSlot(0);
                        if (ex.TryGetValue(srcSlot, out var evalTarget))
                        {
                            var evalExpr = new UnaryExpression(evalTarget, UnaryOp.Eval);
                            expList.Add(evalExpr);
                        }
                    }
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
                            call.HasOmittedArguments = true;
                        }
                        else if (paramCount == -2)
                        {
                            foreach (var reg in ins.Registers.Skip(3).OfType<RegisterParameter>())
                            {
                                var pSlot = reg.GetSlot();
                                if (ex.TryGetValue(pSlot, out var arg))
                                {
                                    // 检测展开参数标记
                                    if (reg.ParameterExpand == FuncParameterExpand.FatExpand)
                                    {
                                        call.SpreadParameterIndices ??= new HashSet<int>();
                                        call.SpreadParameterIndices.Add(call.Parameters.Count);
                                    }

                                    arg.Parent = call;
                                    call.Parameters.Add(arg);
                                }
                            }
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
                            call.HasOmittedArguments = true;
                        }
                        else if (paramCount == -2)
                        {
                            foreach (var reg in ins.Registers.Skip(4).OfType<RegisterParameter>())
                            {
                                var pSlot = reg.GetSlot();
                                if (ex.TryGetValue(pSlot, out var arg))
                                {
                                    // 检测展开参数标记
                                    if (reg.ParameterExpand == FuncParameterExpand.FatExpand)
                                    {
                                        call.SpreadParameterIndices ??= new HashSet<int>();
                                        call.SpreadParameterIndices.Add(call.Parameters.Count);
                                    }

                                    arg.Parent = call;
                                    call.Parameters.Add(arg);
                                }
                            }
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
                            call.HasOmittedArguments = true;
                        }
                        else if (paramCount == -2)
                        {
                            foreach (var reg in ins.Registers.Skip(4).OfType<RegisterParameter>())
                            {
                                var pSlot = reg.GetSlot();
                                if (ex.TryGetValue(pSlot, out var arg))
                                {
                                    // 检测展开参数标记
                                    if (reg.ParameterExpand == FuncParameterExpand.FatExpand)
                                    {
                                        call.SpreadParameterIndices ??= new HashSet<int>();
                                        call.SpreadParameterIndices.Add(call.Parameters.Count);
                                    }

                                    arg.Parent = call;
                                    call.Parameters.Add(arg);
                                }
                            }
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
                            call.HasOmittedArguments = true;
                        }
                        else if (paramCount == -2)
                        {
                            foreach (var reg in ins.Registers.Skip(3).OfType<RegisterParameter>())
                            {
                                var pSlot = reg.GetSlot();
                                if (ex.TryGetValue(pSlot, out var arg))
                                {
                                    arg.Parent = call;
                                    call.Parameters.Add(arg);
                                }
                            }
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
                        if (dst == 0) //just execute and discard result
                        {
                            expList.Add(call);
                        }
                    }
                        break;
                    case OpCode.GPD:
                    {
                        var dst = ins.GetRegisterSlot(0);
                        var slot = ins.GetRegisterSlot(1);
                        var instance = ex[slot];
                        var name = ins.Data.AsString();
                        var newId = new IdentifierExpression(name) {Instance = instance};
                        ex[dst] = newId;
                    }
                        break;
                    case OpCode.GPDS:
                    {
                        var dst = ins.GetRegisterSlot(0);
                        var slot = ins.GetRegisterSlot(1);
                        var instance = ex[slot];
                        var name = ins.Data.AsString();
                        var property = new IdentifierExpression(name) {Instance = instance};
                        ex[dst] = new UnaryExpression(property, UnaryOp.PropertyRef);
                    }
                        break;
                    case OpCode.GPI:
                    {
                        var dst = ins.GetRegisterSlot(0);
                        var obj = ins.GetRegisterSlot(1);
                        var name = ins.GetRegisterSlot(2);

                        PropertyAccessExpression p = new PropertyAccessExpression(ex[name], ex[obj]);
                        ex[dst] = p;
                    }
                        break;
                    case OpCode.GPIS:
                    {
                        var dst = ins.GetRegisterSlot(0);
                        var obj = ins.GetRegisterSlot(1);
                        var name = ins.GetRegisterSlot(2);
                        var access = new PropertyAccessExpression(ex[name], ex[obj]);
                        ex[dst] = new UnaryExpression(access, UnaryOp.PropertyRef);
                    }
                        break;
                    case OpCode.SPI:
                    case OpCode.SPIE:
                    case OpCode.SPIS:
                    {
                        var obj = ins.GetRegisterSlot(0);
                        var name = ins.GetRegisterSlot(1);
                        var src = ins.GetRegisterSlot(2);

                        // 如果目标是字典构造器，将键值对合并到构造器参数中
                        // 这样 new Dict() + spis key, val 会变成 %[ key => val ]
                        if (ins.OpCode == OpCode.SPIS &&
                            ex[obj] is InvokeExpression dictCtor &&
                            dictCtor.InvokeType == InvokeType.Ctor &&
                            IsCollectionCtor(dictCtor) &&
                            dictCtor.MethodExpression is IdentifierExpression dictId &&
                            dictId.FullName == "global.Dictionary")
                        {
                            dictCtor.Parameters.Add(ex[name]);  // key
                            dictCtor.Parameters.Add(ex[src]);   // value
                            break;
                        }

                        Expression left = new PropertyAccessExpression(ex[name], ex[obj]);
                        BinaryExpression b = new BinaryExpression(left,
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
                        var objSlot = ins.GetRegisterSlot(0);
                        var ident = new IdentifierExpression(ins.Data.AsString())
                            {Instance = ex[objSlot]};
                        Expression left = ident;
                        bool isPropertyRef = ins.OpCode == OpCode.SPDS && objSlot != Const.ThisReg && objSlot != Const.ThisProxyReg;
                        if (isPropertyRef)
                        {
                            left = new UnaryExpression(ident, UnaryOp.PropertyRef);
                        }
                        var right = ex[ins.GetRegisterSlot(2)];
                        BinaryExpression b = new BinaryExpression(left, right, BinaryOp.Assign);
                        //check declare (SPDS with PropertyRef is never a declaration)
                        if (!isPropertyRef && context.Object.ContextType == TjsContextType.TopLevel)
                        {
                            b.IsDeclaration = true;
                            if (!context.RegisteredMembers.ContainsKey(ident.Name))
                            {
                                var stub = new TjsStub();
                                if (right is ConstantExpression con) //TODO: better type check
                                {
                                    stub.Type = con.DataType;
                                }

                                context.RegisteredMembers[ident.Name] = stub;
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
                        var dst = ins.GetRegisterSlot(0);
                        var src = ins.GetRegisterSlot(1);
                        if (ex.TryGetValue(src, out var srcExpr))
                        {
                            ex[dst] = new UnaryExpression(srcExpr, UnaryOp.PropertyObject);
                        }
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

            // 后处理：将赋值内联到后续调用参数中。
            // 检测模式：expList[i] = BinaryExpression(Assign, left, right)，
            // 且 expList[j>i] 是 InvokeExpression，其某个参数与 right 是同一引用。
            // 此时将该参数替换为赋值表达式本身，实现 foo(obj.prop = val) 的内联。
            InlineAssignmentIntoCallArgs(expList);

            expList.RemoveAll(node => node is Expression exp && exp.Parent != null);

            //Save states
            ex[Const.FlagReg] = flag;
            context.BlockFinalStates[block] = new Dictionary<int, Expression>(ex);

            //Process next
            foreach (var succ in block.To)
            {
                //BlockProcess(context, succ, new Dictionary<int, Expression>(ex)); //TODO: validate if deep copy ex is correct
                BlockProcess(context, succ);
            }
        }

        /// <summary>
        /// 将赋值表达式内联到后续调用参数中。
        /// 当赋值的右侧表达式被后续调用作为参数使用时，
        /// 将参数替换为整个赋值表达式（如 foo(a = expr)）。
        /// 同时处理 ConditionExpression 中的调用（包括被 NOT 包装的情况）。
        /// </summary>
        private static void InlineAssignmentIntoCallArgs(List<IAstNode> expList)
        {
            for (int i = expList.Count - 2; i >= 0; i--)
            {
                if (expList[i] is not BinaryExpression assign || assign.Op != BinaryOp.Assign)
                    continue;

                // 跳过真正的声明（不应内联 var a = x 到调用参数）
                // 属性赋值（如 System.appLockKey = ...）虽可能被误标为声明，但仍可内联
                if (assign.IsDeclaration &&
                    assign.Left is IdentifierExpression leftId &&
                    leftId.Instance == null)
                    continue;

                var right = assign.Right;
                bool inlined = false;

                // 向后查找使用同一引用的调用表达式
                for (int j = i + 1; j < expList.Count && !inlined; j++)
                {
                    if (expList[j] is InvokeExpression invoke)
                    {
                        if (TryInlineIntoInvoke(invoke, right, assign))
                        {
                            inlined = true;
                        }
                    }

                    // 也检查 ConditionExpression 内部的 InvokeExpression
                    // TF 指令会对表达式调用 Invert()，产生 UnaryExpression(Not, InvokeExpression)
                    if (!inlined && expList[j] is ConditionExpression condExpr)
                    {
                        // 从条件中提取 InvokeExpression（可能被 NOT 包装）
                        var condTarget = condExpr.Condition;
                        if (condTarget is UnaryExpression unary && unary.Op == UnaryOp.Not)
                            condTarget = unary.Target;

                        if (condTarget is InvokeExpression condInvoke &&
                            TryInlineIntoInvoke(condInvoke, right, assign))
                        {
                            inlined = true;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 尝试将赋值表达式内联到调用的参数中。
        /// </summary>
        private static bool TryInlineIntoInvoke(InvokeExpression invoke, Expression right, BinaryExpression assign)
        {
            for (int k = 0; k < invoke.Parameters.Count; k++)
            {
                if (ReferenceEquals(invoke.Parameters[k], right))
                {
                    invoke.Parameters[k] = assign;
                    assign.Parent = invoke;
                    return true;
                }
            }

            return false;
        }

        private static void TryAnnotateConditionalPhi(Block mergeBlock, List<Block> froms, PhiExpression phi)
        {
            if (phi == null || froms == null || froms.Count != 2 || phi.PossibleExpressions.Count != 2)
            {
                return;
            }

            static bool TryAssignFromCondition(ConditionExpression cond, List<Block> preds, PhiExpression targetPhi, Block targetMerge)
            {
                if (cond?.Condition == null)
                {
                    return false;
                }

                // 直接跳到 merge：沿用原有逻辑
                if (cond.TrueBranch == targetMerge.Start || cond.FalseBranch == targetMerge.Start)
                {
                    return false;
                }

                var trueIdx = preds.FindIndex(b => b.Start == cond.TrueBranch);
                var falseIdx = preds.FindIndex(b => b.Start == cond.FalseBranch);
                if (trueIdx < 0 || falseIdx < 0 || trueIdx == falseIdx)
                {
                    return false;
                }

                targetPhi.Condition = cond;
                targetPhi.ThenBranch = targetPhi.PossibleExpressions[trueIdx];
                targetPhi.ElseBranch = targetPhi.PossibleExpressions[falseIdx];
                return true;
            }
            
            static bool TryBuildShortCircuitAndCondition(Block condBlock, Block elsePred, Expression innerCond, out Expression combined)
            {
                static bool CanReach(Block current, Block target, int depth, HashSet<Block> visited)
                {
                    if (current == null || target == null || depth < 0 || !visited.Add(current))
                    {
                        return false;
                    }

                    if (current.Start == target.Start)
                    {
                        return true;
                    }

                    if (depth == 0)
                    {
                        return false;
                    }

                    foreach (var next in current.To)
                    {
                        if (CanReach(next, target, depth - 1, visited))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                static bool BranchFlowsTo(Block conditionBlock, int branchStart, Block target)
                {
                    if (conditionBlock == null || target == null)
                    {
                        return false;
                    }

                    if (branchStart == target.Start)
                    {
                        return true;
                    }

                    var branchHead = conditionBlock.To.FirstOrDefault(b => b.Start == branchStart);
                    if (branchHead == null)
                    {
                        return false;
                    }

                    return CanReach(branchHead, target, 6, new HashSet<Block>());
                }

                combined = null;
                if (condBlock == null || elsePred == null || innerCond == null)
                {
                    return false;
                }

                var candidates = elsePred.From
                    .Concat(elsePred.From.SelectMany(b => b.From))
                    .Distinct();
                foreach (var parent in candidates)
                {
                    if (parent == null || ReferenceEquals(parent, condBlock) ||
                        parent.Statements?.Any(s => s is ConditionExpression) != true)
                    {
                        continue;
                    }

                    var parentCond = (ConditionExpression)parent.Statements.Last(s => s is ConditionExpression);
                    var trueToCond = BranchFlowsTo(parent, parentCond.TrueBranch, condBlock);
                    var falseToCond = BranchFlowsTo(parent, parentCond.FalseBranch, condBlock);
                    var trueToElse = BranchFlowsTo(parent, parentCond.TrueBranch, elsePred);
                    var falseToElse = BranchFlowsTo(parent, parentCond.FalseBranch, elsePred);
                    if (!(trueToCond && falseToElse) && !(falseToCond && trueToElse))
                    {
                        continue;
                    }

                    var outerCond = parentCond.Condition;
                    if (falseToCond)
                    {
                        outerCond = new UnaryExpression(outerCond, UnaryOp.Not);
                    }

                    combined = new BinaryExpression(outerCond, innerCond, BinaryOp.LogicAnd);
                    return true;
                }

                return false;
            }

            var condPred = froms.FirstOrDefault(b => b.Statements?.Any(s => s is ConditionExpression) == true);
            if (condPred != null)
            {
                var cond = (ConditionExpression)condPred.Statements.Last(s => s is ConditionExpression);
                if (TryAssignFromCondition(cond, froms, phi, mergeBlock))
                {
                    var falsePred = froms.FirstOrDefault(b => b.Start == cond.FalseBranch);
                    // 对 FlagReg 和非 FlagReg（三元结果寄存器）均应用短路与条件合并，
                    // 以确保形如 (outerCond && innerCond) ? A : B 的三元条件被完整重建
                    if (TryBuildShortCircuitAndCondition(condPred, falsePred, cond.Condition, out var combinedCond))
                    {
                        phi.Condition = new ConditionExpression(combinedCond, cond.JumpIf)
                        {
                            JumpTo = cond.JumpTo,
                            ElseTo = cond.ElseTo
                        };
                    }
                    return;
                }

                // 兼容旧路径：条件块直接跳到 merge
                if (cond.TrueBranch != mergeBlock.Start && cond.FalseBranch != mergeBlock.Start)
                {
                    return;
                }

                var condPredIdx = froms.IndexOf(condPred);
                var otherIdx = condPredIdx == 0 ? 1 : 0;
                var condPredValue = phi.PossibleExpressions[condPredIdx];
                var otherValue = phi.PossibleExpressions[otherIdx];
                var trueToMerge = cond.TrueBranch == mergeBlock.Start;

                // 使用 cond 的浅拷贝，避免 ControlFlowPass 的 MergeIfCondition 修改原始
                // ConditionExpression.Condition 时影响 phi 的短路语义判断（ThenBranch/cond 引用关系）
                phi.Condition = new ConditionExpression(cond.Condition, cond.JumpIf)
                {
                    JumpTo = cond.JumpTo,
                    ElseTo = cond.ElseTo
                };
                phi.ThenBranch = trueToMerge ? condPredValue : otherValue;
                phi.ElseBranch = trueToMerge ? otherValue : condPredValue;
                return;
            }

            // 菱形分支：merge 的前驱不带条件，条件位于共同上游块
            var parentCond = froms
                .SelectMany(b => b.From)
                .Distinct()
                .FirstOrDefault(b => b.Statements?.Any(s => s is ConditionExpression) == true);
            if (parentCond == null)
            {
                return;
            }

            var parentCondition = (ConditionExpression)parentCond.Statements.Last(s => s is ConditionExpression);
            if (!TryAssignFromCondition(parentCondition, froms, phi, mergeBlock))
            {
                return;
            }

            var parentFalsePred = froms.FirstOrDefault(b => b.Start == parentCondition.FalseBranch);
            // 对所有寄存器均应用短路与条件合并，以完整重建菱形分支模式下的短路三元条件
            if (TryBuildShortCircuitAndCondition(parentCond, parentFalsePred, parentCondition.Condition, out var parentCombinedCond))
            {
                phi.Condition = new ConditionExpression(parentCombinedCond, parentCondition.JumpIf)
                {
                    JumpTo = parentCondition.JumpTo,
                    ElseTo = parentCondition.ElseTo
                };
            }
        }
    }
}