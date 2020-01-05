using System.Collections.Generic;
using System.Linq;
using Furikiri.Emit;
using Superpower;
using Superpower.Parsers;

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

        private static readonly TokenListParser<TjsAsmToken, ITjsVariant> OctetVariant =
            from typeDesc in Token.EqualTo(TjsAsmToken.TypeDescription).Apply(TjsAsmTokenizer.TypeDescriptionToken)
            where typeDesc == TjsVarType.Octet.ToTjsTypeName()
            from val in Token.EqualTo(TjsAsmToken.OctetValue).Apply(TjsAsmTokenizer.OctetToken)
            select (ITjsVariant) new TjsOctet(val);

        private static readonly TokenListParser<TjsAsmToken, ITjsVariant> RealVariant =
            from typeDesc in Token.EqualTo(TjsAsmToken.TypeDescription).Apply(TjsAsmTokenizer.TypeDescriptionToken)
            where typeDesc == TjsVarType.Real.ToTjsTypeName()
            from val in Token.EqualTo(TjsAsmToken.RealValue).Apply(Numerics.DecimalDouble)
            select (ITjsVariant) new TjsReal(val);


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
            from val in StringVariant.Or(RealVariant).Or(IntVariant).Or(OctetVariant)
            select (slot, val);

        private static readonly TokenListParser<TjsAsmToken, List<ITjsVariant>> Variants =
            from vals in Variant.Many()
            select VariantsToList(vals);


        private static readonly TokenListParser<TjsAsmToken, IRegister> RegisterRef =
            from data in Token.EqualTo(TjsAsmToken.Register).Apply(TjsAsmTokenizer.RegisterToken)
            select (IRegister)new RegisterRef((short)data);

        private static readonly TokenListParser<TjsAsmToken, IRegister> RegisterValue =
            from data in Token.EqualTo(TjsAsmToken.Const).Apply(TjsAsmTokenizer.ConstToken)
            select (IRegister)new RegisterValue((short)data);

        private static readonly TokenListParser<TjsAsmToken, IRegister> RegisterShort =
            from data in Token.EqualTo(TjsAsmToken.IntValue).Apply(Numerics.IntegerInt32)
            select (IRegister)new RegisterShort((short)data);
        
        private static readonly TokenListParser<TjsAsmToken, IRegister[]> Register =
            from regs in RegisterRef.Or(RegisterShort).Or(RegisterValue).ManyDelimitedBy(Token.EqualTo(TjsAsmToken.Comma)) //TODO: calld %0, %1.*2(%3)
            select regs;

        private static readonly TokenListParser<TjsAsmToken, Instruction> InstructionParser =
            from label in Token.EqualTo(TjsAsmToken.Label).Apply(TjsAsmTokenizer.LabelToken).Optional()
            from opCode in Token.EqualTo(TjsAsmToken.OpCode).Apply(TjsAsmTokenizer.OpCodeToken)
                       select new Instruction(opCode) {Offset = label ?? 0};

        private static readonly TokenListParser<TjsAsmToken, Method> MethodParser =
            null;
    }
}