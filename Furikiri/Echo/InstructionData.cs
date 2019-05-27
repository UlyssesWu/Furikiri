using System.Collections.Generic;
using System.Linq;
using Furikiri.Emit;
using static Furikiri.Emit.OpCode;

namespace Furikiri.Echo
{
    class InstructionData
    {
        public Instruction Instruction { get; set; }

        /// <summary>
        /// Def
        /// </summary>
        public HashSet<int> Write { get; set; }

        /// <summary>
        /// Use
        /// </summary>
        public HashSet<int> Read { get; set; }

        /// <summary>
        /// variables alive before this
        /// </summary>
        public HashSet<int> LiveIn { get; set; }

        /// <summary>
        /// variables alive after this
        /// </summary>
        public HashSet<int> LiveOut { get; set; }

        /// <summary>
        /// Dead in this
        /// </summary>
        public HashSet<int> Dead { get; set; }

        public InstructionData(Instruction ins)
        {
            Instruction = ins;
        }

        public void ComputeUseDef()
        {
            Write = new HashSet<int>();
            Read = new HashSet<int>();
            Dead = new HashSet<int>();

            var ins = Instruction;
            switch (ins.OpCode)
            {
                case CONST:
                case CP:
                case CL:
                case INCPD:
                case INCPI:
                case INCP:
                case DECPD:
                case DECPI:
                case DECP:
                case LORPD:
                case LORPI:
                case LORP:
                case LANDPD:
                case LANDPI:
                case LANDP:
                case BORPD:
                case BORPI:
                case BORP:
                case BXORPD:
                case BXORPI:
                case BXORP:
                case BANDPD:
                case BANDPI:
                case BANDP:
                case SARPD:
                case SARPI:
                case SARP:
                case SALPD:
                case SALPI:
                case SALP:
                case SRPD:
                case SRPI:
                case SRP:
                case ADDPD:
                case ADDPI:
                case ADDP:
                case SUBPD:
                case SUBPI:
                case SUBP:
                case MODPD:
                case MODPI:
                case MODP:
                case DIVPD:
                case DIVPI:
                case DIVP:
                case IDIVPD:
                case IDIVPI:
                case IDIVP:
                case MULPD:
                case MULPI:
                case MULP:
                case TYPEOF:
                case TYPEOFD:
                case TYPEOFI:
                case CALL:
                case CALLD:
                case CALLI:
                case NEW:
                case GPD:
                case GPI:
                case GPDS:
                case GPIS:
                case GETP:
                case GLOBAL:
                    Write.Add(ins.GetRegisterSlot(0));
                    break;
                case CCL:
                    Write.AddRange(Enumerable.Range(ins.GetRegisterSlot(0), ins.GetRegisterSlot(1)));
                    break;
            }

            foreach (var reg in ins.Registers)
            {
                if (reg is RegisterRef r)
                {
                    if (Write.Contains(r.Slot))
                    {
                        continue;
                    }

                    Read.Add(r.Slot);
                }
            }
            //TODO: op such as ADD, reg[0] is both read and wrote
        }
    }
}
