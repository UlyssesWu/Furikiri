using System;
using System.Collections.Generic;
using System.Linq;
using Furikiri.AST;
using Furikiri.AST.Expressions;
using Furikiri.Emit;

namespace Furikiri.Echo.Pass
{
    partial class ExpressionPass
    {
        /// <summary>
        /// 记录一条 VM 指令产生的临时值身份。switch 的选择表达式只求值一次，
        /// 后续多个比较块会反复读取同一临时槽；槽位会被复用，因此还必须保存
        /// 定义指令位置，才能与源码中多次独立读取同一属性或表达式区分开。
        /// </summary>
        private static T MarkCachedEvaluation<T>(
            T expression, short destination, Instruction instruction)
            where T : Expression
        {
            if (expression != null && destination > Const.ArgBase)
            {
                expression.CachedTemporarySlot = destination;
                expression.CachedEvaluationId = instruction.Line;
            }

            return expression;
        }

        /// <summary>
        /// CP 把命名局部或参数读入临时寄存器时，临时值代表一次新的快照求值。
        /// 不能直接在原 LocalExpression 上写缓存身份，否则该局部后续独立读取
        /// 也会继承旧身份，被误认为仍在同一个 switch 中。
        /// </summary>
        private static Expression SnapshotCopiedEvaluation(
            Expression source, short destination, Instruction instruction)
        {
            if (source == null || destination <= Const.ArgBase ||
                source.CachedEvaluationId.HasValue)
            {
                return source;
            }

            Expression snapshot = source switch
            {
                LocalExpression local => new LocalExpression(local.VariableDef)
                {
                    DataType = local.DataType
                },
                IdentifierExpression identifier => new IdentifierExpression(
                    identifier.Name, identifier.IdentifierType)
                {
                    Instance = identifier.Instance,
                    HideInstance = identifier.HideInstance
                },
                ConstantExpression constant =>
                    new ConstantExpression(constant.Variant),
                _ => source
            };

            return MarkCachedEvaluation(snapshot, destination, instruction);
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

            for (var suffix = 0;; suffix++)
            {
                var candidate = $"{baseName}_{suffix}";
                if (!usedNames.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private static LocalExpression GetLocalExpression(DecompileContext context, short slot)
        {
            if (slot <= Const.ArgBase)
            {
                if (!context.Vars.TryGetValue(slot, out var variable))
                {
                    variable = new Variable(slot, context.Object);
                    context.Vars[slot] = variable;
                }

                return new LocalExpression(variable);
            }

            return new LocalExpression(context.Object, slot);
        }
    }
}
