﻿using System.Collections.Generic;

namespace Furikiri.AST.Expressions
{
    class ConditionExpression : Expression, IJump
    {
        public override AstNodeType Type => AstNodeType.ConditionExpression;
        public override IEnumerable<IAstNode> Children => null;

        public Expression Condition { get; set; }

        /// <summary>
        /// Jump if true or false
        /// </summary>
        public bool JumpIf { get; set; }

        /// <summary>
        /// If jump, goto where
        /// </summary>
        public int JumpTo { get; set; }

        /// <summary>
        /// If not jump, goto where
        /// </summary>
        public int ElseTo { get; set; }

        public int TrueBranch => JumpIf ? JumpTo : ElseTo;
        public int FalseBranch => JumpIf ? ElseTo : JumpTo;

        public ConditionExpression(Expression condition, bool jumpIf = true)
        {
            Condition = condition;
            JumpIf = jumpIf;
        }
    }
}