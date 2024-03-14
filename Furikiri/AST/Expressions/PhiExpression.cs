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
        public override IEnumerable<IAstNode> Children => PossibleExpressions;

        public int Slot { get; set; }

        public List<Expression> PossibleExpressions { get; set; } = new List<Expression>();

        public PhiExpression(int slot)
        {
            Slot = slot;
        }

        public override string ToString()
        {
            return $"{string.Join(" || ", PossibleExpressions)}";
        }
    }
}