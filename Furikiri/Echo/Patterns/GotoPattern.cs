using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Furikiri.Emit;

namespace Furikiri.Echo.Patterns
{
    public enum JumpType
    {
        Direct,
        IfTrue,
        IfFalse,
    }

    class GotoPattern : IBranch, ITerminal
    {
        public bool Terminal { get; set; } = true;
        public HashSet<int> Write { get; set; }
        public HashSet<int> Read { get; set; }
        public HashSet<int> LiveIn { get; set; }
        public HashSet<int> LiveOut { get; set; }
        public void ComputeUseDefs()
        {
        }

        public int Length => 1;
        public BranchType BranchType => BranchType.Goto;
        public List<Block> Content { get; }
        public IExpression Condition { get; }
        public JumpType JumpType { get; set; }
        public Block Dst { get; set; }

        public static GotoPattern Match(List<Instruction> codes, int i, DecompileContext context)
        {
            var ins = codes[i];
            if (ins.OpCode.IsJump())
            {
                var jmpData = (JumpData) ins.Data;
                var jmp = new GotoPattern();
                jmp.Dst = context.Blocks.FirstOrDefault(b => b.IsInBlock(jmpData.Goto.Line));
                switch (ins.OpCode)
                {
                    case OpCode.JMP:
                        jmp.JumpType = JumpType.Direct;
                        break;
                    case OpCode.JF:
                        jmp.JumpType = JumpType.IfTrue;
                        break;
                    case OpCode.JNF:
                        jmp.JumpType = JumpType.IfFalse;
                        break;
                }

                return jmp;
            }

            return null;
        }

        public override string ToString()
        {
            var label = Dst == null ? "<unknown>" : $"BLOCK{Dst.Id}";
            return $"goto {label}{Terminal.Term()}";
        }
    }
}