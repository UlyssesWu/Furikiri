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
            return statements.Count == 1 && statements[0] is ConditionExpression;
        }

        public static ConditionExpression GetCondition(this List<IAstNode> statements)
        {
            //old impl
            //if (statements.Count == 1 && statements[0] is ConditionExpression condition)
            //{
            //    return condition;
            //}

            if (statements.LastOrDefault() is ConditionExpression condition)
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
                condition.JumpIf = !condition.JumpIf;
                condition.Condition = condition.Condition.Invert();
                return condition;
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
                    case BinaryOp.Equal:
                        binary.Op = BinaryOp.NotEqual;
                        break;
                    case BinaryOp.NotEqual:
                        binary.Op = BinaryOp.Equal;
                        break;
                    case BinaryOp.Congruent:
                        binary.Op = BinaryOp.NotCongruent;
                        break;
                    case BinaryOp.NotCongruent:
                        binary.Op = BinaryOp.Congruent;
                        break;
                    case BinaryOp.LessThan:
                        binary.Op = BinaryOp.GreaterThan;
                        break;
                    case BinaryOp.GreaterThan:
                        binary.Op = BinaryOp.LessThan;
                        break;
                }

                return binary;
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
                if (parentLevel < binLevel || parentLevel == binLevel && bParent.Op != bin.Op && bParent.Right == bin)
                {
                    return true;
                }
            }

            else if (bin.Parent is UnaryExpression uParent)
            {
                if (uParent.Op.GetPrecedence() < bin.Op.GetPrecedence())
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