using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        private readonly HashSet<(Instruction Instruction, InvokeExpression Constructor)>
            _foldedCollectionWrites = new();
        private readonly Dictionary<InvokeExpression, Instruction>
            _collectionConstructorOrigins = new();
        private readonly Dictionary<InvokeExpression, List<Instruction>>
            _collectionWriteOrigins = new();

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

        /// <summary>
        /// 前向汇合块在补齐最后一个前驱时可能被递归重入。若重入已经执行了当前块
        /// 的 SPIS，来自同一条 NEW 的某个集合副本会比另一个副本多出当前块字段，
        /// 普通 Phi 合并便会把它误判为条件字典。这里只撤销这种“未来写入”：
        /// 构造指令必须相同、既有写入序列必须是前缀，且所有额外写入都属于当前块。
        /// 分支内部真实发生的集合修改来自前驱块，因此不会命中本规则。
        /// </summary>
        private bool TryCollapseReenteredCollectionState(
            Block mergeBlock, IReadOnlyList<Expression> states,
            out Expression collapsed)
        {
            collapsed = null;
            var constructors = states.OfType<InvokeExpression>().ToList();
            if (constructors.Count != states.Count || constructors.Count < 2 ||
                constructors.Any(constructor => !IsCollectionCtor(constructor) ||
                    !_collectionConstructorOrigins.ContainsKey(constructor) ||
                    !_collectionWriteOrigins.ContainsKey(constructor)))
            {
                return false;
            }

            var origin = _collectionConstructorOrigins[constructors[0]];
            if (constructors.Any(constructor =>
                    !ReferenceEquals(_collectionConstructorOrigins[constructor], origin)))
            {
                return false;
            }

            var shortest = constructors
                .OrderBy(constructor => _collectionWriteOrigins[constructor].Count)
                .First();
            var prefix = _collectionWriteOrigins[shortest];
            if (constructors.All(constructor =>
                    _collectionWriteOrigins[constructor].Count == prefix.Count))
            {
                return false;
            }

            foreach (var constructor in constructors)
            {
                var writes = _collectionWriteOrigins[constructor];
                if (writes.Count < prefix.Count ||
                    !writes.Take(prefix.Count).SequenceEqual(prefix) ||
                    writes.Skip(prefix.Count).Any(instruction =>
                        !mergeBlock.Instructions.Contains(instruction)))
                {
                    return false;
                }
            }

            collapsed = shortest;
            return true;
        }
    }
}
