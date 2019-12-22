using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using Furikiri.Emit;
using Superpower;
using Superpower.Display;
using Superpower.Model;
using Superpower.Parsers;
using Superpower.Tokenizers;

namespace Furikiri.Compile
{
    public static class TjsAsmParser
    {
        private static readonly TokenListParser<TjsAsmToken, int> VariantDeclare =
            from slot in Token.EqualTo(TjsAsmToken.Const).Apply(TjsAsmTokenizer.ConstToken)
            from equal in Token.EqualTo(TjsAsmToken.Assign)
            select slot;

        private static readonly TokenListParser<TjsAsmToken, ITjsVariant> StringVariant =
            from typeDesc in Token.EqualTo(TjsAsmToken.TypeDescription).Apply(TjsAsmTokenizer.TypeDescriptionToken)
            where typeDesc == TjsVarType.String.ToTjsTypeName()
            from val in Token.EqualTo(TjsAsmToken.StringValue).Apply(QuotedString.CStyle)
            select (ITjsVariant) new TjsString(val);

        private static readonly TokenListParser<TjsAsmToken, ITjsVariant> IntVariant =
            from typeDesc in Token.EqualTo(TjsAsmToken.TypeDescription).Apply(TjsAsmTokenizer.TypeDescriptionToken)
            where typeDesc == TjsVarType.Int.ToTjsTypeName()
            from val in Token.EqualTo(TjsAsmToken.IntValue).Apply(Numerics.IntegerInt32)
            select (ITjsVariant) new TjsInt(val);


        private static List<ITjsVariant> VariantsToList(IReadOnlyCollection<(int id, ITjsVariant val)> tjsVars)
        {
            var list = new List<ITjsVariant>(tjsVars.Count);
            foreach (var v in tjsVars.OrderBy(v => v.id))
            {
                list.Add(v.val);
            }

            return list;
        }

        private static readonly TokenListParser<TjsAsmToken, (int id, ITjsVariant val)> Variant =
            from slot in VariantDeclare
            from val in StringVariant
            select (slot, val);

        private static readonly TokenListParser<TjsAsmToken, List<ITjsVariant>> Variants =
            from vals in Variant.Many()
            select VariantsToList(vals);

        private static readonly TokenListParser<TjsAsmToken, Instruction> InstructionParser =
            null;

        private static readonly TokenListParser<TjsAsmToken, Method> MethodParser =
            null;
    }
}