using System.Collections.Generic;

// ReSharper disable CommentTypo

namespace Furikiri.AST.Expressions
{
    /// <summary>
    /// Expression for undetermined variable
    /// </summary>
    /// Elapsam semel occasionem non ipse potest Iuppiter reprehendere https://zh.moegirl.org/Phi
    class PhiExpression : Expression
    {
        public override AstNodeType Type => AstNodeType.PhiExpression;
        public override IEnumerable<IAstNode> Children => new[] {ThenBranch, ElseBranch};

        public int Slot { get; set; }

        public ConditionExpression Condition { get; set; }

        public Expression ThenBranch { get; set; }
        public Expression ElseBranch { get; set; }

        public PhiExpression(int slot)
        {
            Slot = slot;
        }

        public override string ToString()
        {
            return $"{ThenBranch} || {ElseBranch}";
        }
    }
}