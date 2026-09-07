using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Furikiri.AST.Expressions;
using Furikiri.Echo;
using Furikiri.Emit;

namespace Furikiri.AST
{
    internal static class AstExtensions
    {
        public static bool IsCondition(this List<IAstNode> statements)
        {
            return statements?.Count == 1 && statements[0] is ConditionExpression;
        }

        public static ConditionExpression GetCondition(this List<IAstNode> statements)
        {
            //old impl
            //if (statements.Count == 1 && statements[0] is ConditionExpression condition)
            //{
            //    return condition;
            //}

            if (statements?.LastOrDefault() is ConditionExpression condition)
            {
                return condition;
            }

            return null;
        }

        /// <summary>
        /// !this
        /// </summary>
        /// <param name="exp"></param>
        /// <returns></returns>
        public static Expression Invert(this Expression exp)
        {
            if (exp is ConditionExpression condition)
            {
                // 返回新实例，避免原地修改共享 ConditionExpression 影响 phi 的 ThenBranch/ElseBranch 关联
                return new ConditionExpression(condition.Condition.Invert(), !condition.JumpIf)
                {
                    JumpTo = condition.JumpTo,
                    ElseTo = condition.ElseTo
                };
            }

            if (exp is UnaryExpression unary)
            {
                if (unary.Op == UnaryOp.Not)
                {
                    return unary.Target;
                }
            }

            if (exp is BinaryExpression binary)
            {
                switch (binary.Op)
                {
                    // 比较运算符：返回新实例，避免原地修改共享引用对象导致双重反转
                    case BinaryOp.Equal:
                        return new BinaryExpression(binary.Left, binary.Right, BinaryOp.NotEqual);
                    case BinaryOp.NotEqual:
                        return new BinaryExpression(binary.Left, binary.Right, BinaryOp.Equal);
                    case BinaryOp.Congruent:
                        return new BinaryExpression(binary.Left, binary.Right, BinaryOp.NotCongruent);
                    case BinaryOp.NotCongruent:
                        return new BinaryExpression(binary.Left, binary.Right, BinaryOp.Congruent);
                    case BinaryOp.LessThan:
                        return new BinaryExpression(binary.Left, binary.Right, BinaryOp.GreaterOrEqual);
                    case BinaryOp.GreaterThan:
                        return new BinaryExpression(binary.Left, binary.Right, BinaryOp.LessOrEqual);
                    case BinaryOp.GreaterOrEqual:
                        return new BinaryExpression(binary.Left, binary.Right, BinaryOp.LessThan);
                    case BinaryOp.LessOrEqual:
                        return new BinaryExpression(binary.Left, binary.Right, BinaryOp.GreaterThan);
                    // De Morgan 定律: !(A || B) => !A && !B, !(A && B) => !A || !B。
                    // 路径谓词会在多个分支间共享同一表达式节点，因此也必须返回
                    // 新树；原地修改会让先前缓存的谓词方向随之后的取反一起改变。
                    case BinaryOp.LogicOr:
                        return new BinaryExpression(
                            binary.Left.Invert(), binary.Right.Invert(), BinaryOp.LogicAnd);
                    case BinaryOp.LogicAnd:
                        return new BinaryExpression(
                            binary.Left.Invert(), binary.Right.Invert(), BinaryOp.LogicOr);
                    default:
                        // 位运算、算术等二元式没有可直接翻转的对应运算符。
                        // TF/NF 仍必须保留逻辑取反，不能原样返回表达式；例如
                        // `!(shift & ssShift)` 若丢掉 ! 会把按键分支方向完全颠倒。
                        return new UnaryExpression(binary, UnaryOp.Not);
                }

            }

            return new UnaryExpression(exp, UnaryOp.Not);
        }

        /// <summary>
        /// this || expression
        /// </summary>
        /// <param name="left"></param>
        /// <param name="right"></param>
        /// <returns></returns>
        public static BinaryExpression Or(this Expression left, Expression right)
        {
            while (right is ConditionExpression condition)
            {
                right = condition.Condition;
            }

            while (left is ConditionExpression condition)
            {
                left = condition.Condition;
            }

            return new BinaryExpression(left, right, BinaryOp.LogicOr);
        }

        /// <summary>
        /// this && expression
        /// </summary>
        /// <param name="left"></param>
        /// <param name="right"></param>
        /// <returns></returns>
        public static BinaryExpression And(this Expression left, Expression right)
        {
            while (right is ConditionExpression condition)
            {
                right = condition.Condition;
            }

            while (left is ConditionExpression condition)
            {
                left = condition.Condition;
            }

            return new BinaryExpression(left, right, BinaryOp.LogicAnd);
        }

        public static bool NeedBrackets(this BinaryExpression bin)
        {
            if (bin.Parent is BinaryExpression bParent)
            {
                var binLevel = bin.Op.GetPrecedence();
                var parentLevel = bParent.Op.GetPrecedence();
                var isRightOperand = ReferenceEquals(bParent.Right, bin);
                // 二元运算通常按左结合解析。同优先级的右子树即使运算符相同也
                // 不能一概展开：TJS2 的 + 会随运行时类型执行数值相加或字符串
                // 拼接，`prefix + (line + 1)` 与 `prefix + line + 1` 语义不同。
                // 自赋值 AST 仍保存为 `left + right`，但写出时会变为 `left += right`，
                // 此时整个 right 已由赋值语法自然成组，不应再输出多余括号。
                // 其余情况仅对明确可安全重组的短路/位运算和普通赋值省略括号。
                var canFlattenRight = bParent.IsSelfAssignment ||
                    bParent.Op == bin.Op &&
                    bParent.Op is BinaryOp.LogicAnd or BinaryOp.LogicOr or
                        BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor or
                        BinaryOp.Assign;
                if (parentLevel < binLevel ||
                    parentLevel == binLevel && isRightOperand && !canFlattenRight)
                {
                    return true;
                }
            }

            else if (bin.Parent is UnaryExpression uParent)
            {
                // 一元前缀运算符的目标若是完整二元式，必须显式成组。
                // 尤其 `!(x instanceof "Function")` 不能写成
                // `!x instanceof "Function"`，后者会先对 x 取反。
                // int()/string()/后置 eval 等自身已有括号，不在此重复添加。
                if (uParent.Op is UnaryOp.Not or UnaryOp.BitNot or
                    UnaryOp.InvertSign or UnaryOp.ToNumber or UnaryOp.IsFalse or
                    UnaryOp.TypeOf or UnaryOp.Invalidate or UnaryOp.IsValid or
                    UnaryOp.PropertyRef or UnaryOp.ToCharacterCode or
                    UnaryOp.FromCharacterCode)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// To RegExp format
        /// <example>/start(.*?)end/gi</example>
        /// </summary>
        /// <param name="invoke"></param>
        /// <returns></returns>
        public static string ToRegExp(this InvokeExpression invoke)
        {
            if (invoke.InvokeType != InvokeType.RegExpCompile || invoke.Parameters.Count < 1 ||
                !(invoke.Parameters[0] is ConstantExpression constant) || !(constant.Variant is TjsString tStr))
            {
                throw new ArgumentException("The Expression is not a RegExp");
            }

            var regex = tStr.StringValue;
            if (!regex.StartsWith("//"))
            {
                return regex; //TODO: maybe wrong but who cares
            }

            StringBuilder sb = new StringBuilder();
            sb.Append('/');
            var count = 0;
            while (regex[2 + count] != '/')
            {
                count++;
            }

            sb.Append(regex.Substring(2 + count + 1)).Append('/');

            if (count > 0)
            {
                sb.Append(regex.Substring(2, count));
            }

            return sb.ToString();
        }
    }
}
