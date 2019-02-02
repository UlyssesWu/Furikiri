using System.Collections.Generic;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    /// <summary>
    /// <example>a+b</example>
    /// </summary>
    class BinaryOpPattern : IExpression, ITerminal
    {
        public int Length => 1;
        public short Slot { get; private set; }
        public IExpression Left { get; set; }
        public IExpression Right { get; set; }
        public BinaryOp Op { get; set; }

        public bool Terminal { get; set; }
        public HashSet<int> Write { get; set; }
        public HashSet<int> Read { get; set; }
        public HashSet<int> LiveIn { get; set; }
        public HashSet<int> LiveOut { get; set; }
        public HashSet<int> Dead { get; set; }

        public void ComputeUseDefs()
        {
            Write = new HashSet<int>();
            Read = new HashSet<int>();
            Dead = new HashSet<int>();
            if (Op == BinaryOp.Assign && Left is LocalPattern l)
            {
                Write.Add(l.Slot);
            }
            else
            {
                Read.Add(Left.Slot);
            }

            if (Right is LocalPattern r)
            {
                Read.Add(r.Slot);
            }
            else if (Right is ITerminal t && t.Read != null)
            {
                Read.AddRange(t.Read);
                if (t is BinaryOpPattern b && b.Op == BinaryOp.Assign)
                {
                    Read.Add(b.Left.Slot);
                }
            }
        }

        public bool IsDeclaration { get; set; }

        /// <summary>
        /// Left Type
        /// </summary>
        public TjsVarType Type => Left.Type != TjsVarType.Null ? Left.Type : Right.Type;

        public static BinaryOpPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            BinaryOpPattern b = new BinaryOpPattern();
            switch (codes[i].OpCode)
            {
                case OpCode.ADD:
                case OpCode.SUB:
                case OpCode.MUL:
                case OpCode.DIV:
                case OpCode.IDIV:
                {
                    context.PopExpressionPatterns(codes[i].GetRelatedSlots());
                    var exps = context.Expressions;
                    b.Slot = codes[i].GetRegisterSlot(0);
                    b.Left = exps[b.Slot];
                    b.Right = exps[codes[i].GetRegisterSlot(1)];
                    b.Op = GetOp(codes[i].OpCode);
                    return b;
                }
                case OpCode.SPDS: //this.a = b;
                {
                    context.PopExpressionPatterns();
                    var exps = context.Expressions;
                    var thisSlot = codes[i].GetRegisterSlot(0);
                    var leftSlot = codes[i].GetRegisterSlot(1);
                    var rightSlot = codes[i].GetRegisterSlot(2);
                    b.Right = exps[rightSlot];
                    b.Left = new ChainGetPattern(thisSlot, codes[i].Data.AsString());
                    b.Terminal = true;
                    b.Slot = thisSlot;

                    //check declare
                    if (context.Object.ContextType != TjsContextType.Class &&
                        !context.Vars.ContainsKey(leftSlot))
                    {
                        b.IsDeclaration = true;
                        context.Vars[leftSlot] = new TjsStub(leftSlot, b.Right.Type);
                    }

                    return b;
                }
                case OpCode.CP: //var a = b;
                {
                    var dst = codes[i].GetRegisterSlot(0);
                    var src = codes[i].GetRegisterSlot(1);
                    if (dst < Const.ThisProxy && src > Const.Resource)
                    {
                        //set var
                        context.PopExpressionPatterns();
                        var exps = context.Expressions;
                        LocalPattern l = new LocalPattern(true, dst) {Type = exps[src].Type};
                        b.Left = l;
                        b.Right = exps[src];
                        b.Op = BinaryOp.Assign;
                        context.Expressions[dst] = l; //Which is better?
                        //context.Expressions[dst] = b;
                        b.Terminal = true;
                        b.Slot = l.Slot;
                        //check declare
                        if (!context.Vars.ContainsKey(l.Slot))
                        {
                            b.IsDeclaration = true;
                            context.Vars[l.Slot] = new TjsStub(l.Slot, exps[src].Type);
                        }

                        return b;
                    }

                    return null;
                }
            }

            return null;
        }

        public static BinaryOp GetOp(OpCode code)
        {
            switch (code)
            {
                case OpCode.SPDS:
                    return BinaryOp.Assign;
                case OpCode.ADD:
                    return BinaryOp.Add;
                case OpCode.SUB:
                    return BinaryOp.Sub;
                case OpCode.MUL:
                    return BinaryOp.Mul;
                case OpCode.DIV:
                    return BinaryOp.Div;
                case OpCode.IDIV:
                    return BinaryOp.Idiv;
                default:
                    break;
            }

            return BinaryOp.Unknown;
        }

        public override string ToString()
        {
            if (IsDeclaration)
            {
                return $"var {Left.ToString()} {Op.ToSymbol()} {Right.ToString()}{Terminal.Term()}";
            }

            return $"{Left.ToString()} {Op.ToSymbol()} {Right.ToString()}{Terminal.Term()}";
        }
    }
}