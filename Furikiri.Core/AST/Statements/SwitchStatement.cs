using System.Collections.Generic;
using System.Linq;
using Furikiri.AST.Expressions;

namespace Furikiri.AST.Statements
{
    /// <summary>
    /// 已恢复的 switch 分派。一个子句可以包含多个共用正文的 case 标签。
    /// </summary>
    internal sealed class SwitchStatement : Statement
    {
        public override AstNodeType Type => AstNodeType.SwitchStatement;

        public override IEnumerable<IAstNode> Children =>
            new IAstNode[] { Expression }
                .Concat(Cases.SelectMany(@case => @case.Labels.Cast<IAstNode>()))
                .Concat(Cases.Select(@case => @case.Body))
                .Concat(Default == null
                    ? Enumerable.Empty<IAstNode>()
                    : new IAstNode[] { Default });

        public Expression Expression { get; set; }

        public List<SwitchCaseClause> Cases { get; } =
            new List<SwitchCaseClause>();

        public BlockStatement Default { get; set; }
    }

    internal sealed class SwitchCaseClause
    {
        public List<Expression> Labels { get; } = new List<Expression>();

        public BlockStatement Body { get; set; }
    }
}
