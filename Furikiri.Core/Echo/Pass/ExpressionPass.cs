using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.AST.Statements;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    /// <summary>
    /// 表达式恢复的初始化与工作队列入口。Expression 子目录按值身份、Phi、集合和语句整理拆分；
    /// Instructions 子目录按 opcode 职责处理指令，基本块状态的合并与保存仍集中在 BlockProcess 中。
    /// </summary>
    partial class ExpressionPass : IPass
    {
        private readonly Queue<Block> _pendingBlocks = new();
        private readonly HashSet<Block> _queuedBlocks = new();

        public IdentifierExpression Global = new IdentifierExpression("global", IdentifierType.Global);
        public IdentifierExpression This = new IdentifierExpression("this", IdentifierType.This);
        public IdentifierExpression ThisProxy = new IdentifierExpression("", IdentifierType.ThisProxy);
        public ConstantExpression Void = new ConstantExpression(TjsVoid.Void);

        public BlockStatement Process(DecompileContext context, BlockStatement statement)
        {
            _foldedCollectionWrites.Clear();
            _collectionConstructorOrigins.Clear();
            _collectionWriteOrigins.Clear();
            _pendingBlocks.Clear();
            _queuedBlocks.Clear();
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
                var v = new Variable(slot)
                {
                    IsParameter = true,
                    GeneratedIndex = i
                };
                context.Vars.Add(slot, v);
                exps.Add(slot, new LocalExpression(v));
            }

            // create array params if needed
            var hasUnnamedArray = context.Object.FuncDeclUnnamedArgArrayBase > 0 ||
                context.Blocks.SelectMany(block => block.Instructions)
                    .SelectMany(instruction => instruction.Registers).OfType<RegisterParameter>()
                    .Any(parameter => parameter.ParameterExpand == FuncParameterExpand.FatUnnamedExpand);
            var hasCollapseArray = context.Object.FuncDeclCollapseBase >= 0;
            if (hasCollapseArray)
            {
                short slot = (short)(-context.Object.FuncDeclCollapseBase + Const.ArgBase);
                var array = new Variable(slot)
                {
                    IsParameter = true,
                    IsNamedArray = true,
                    GeneratedIndex = Const.ArgBase - slot,
                    VarType = TjsVarType.Object
                };
                context.Vars.Add(slot, array);
                exps.Add(slot, new LocalExpression(array));
            }
            else if (hasUnnamedArray)
            {
                // 匿名展开参数 `*` 只存在于函数签名和调用语法中，没有可读取的 VM
                // 寄存器。使用不可能出现的虚拟槽保存签名信息，避免与首个局部变量重叠。
                var unnamed = new Variable(short.MinValue)
                {
                    IsParameter = true,
                    IsUnnamedArray = true,
                    VarType = TjsVarType.Object
                };
                context.Vars[short.MinValue] = unnamed;
            }

            BlockProcess(context, entry, exps);
            while (_pendingBlocks.Count > 0)
            {
                BlockProcess(context, _pendingBlocks.Dequeue());
            }

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
    }
}
