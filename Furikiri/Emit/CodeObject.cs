using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Emit
{
    /// <summary>
    /// Internal Code Object, can execute ByteCode (in Krkr, not here)
    /// </summary>
    public class CodeObject : ISourceAccessor
    {
        public Module Block { get; set; }
        public string Name { get; set; }
        public TjsContextType ContextType { get; set; }
        public short[] Code { get; set; }
        public ITjsVariant[] Variants { get; set; }

        public int MaxVariableCount { get; set; }
        public int VariableReserveCount { get; set; }
        public int MaxFrameCount { get; set; }
        public int FuncDeclArgCount { get; set; }
        public int FuncDeclUnnamedArgArrayBase { get; set; }
        public int FuncDeclCollapseBase { get; set; }
        public bool SourcePosArraySorted { get; set; }
        public long[] SourcePosArray { get; set; }
        public int[] SuperClassGetterPointer { get; set; }

        public CodeObject(Module block, string name, int type, short[] code, ITjsVariant[] da,
            int varCount, int varReserveCount, int maxFrame, int argCount, int arrayBase,
            int colBase, bool srcSorted, long[] srcPos, int[] superPointer)
        {
            Block = block;
            Name = name;
            ContextType = (TjsContextType)type;
            Code = code;
            Variants = da;
            MaxVariableCount = varCount;
            VariableReserveCount = varReserveCount;
            MaxFrameCount = maxFrame;
            FuncDeclArgCount = argCount;
            FuncDeclUnnamedArgArrayBase = arrayBase;
            FuncDeclCollapseBase = colBase;
            SourcePosArraySorted = srcSorted;
            SourcePosArray = srcPos;
            SuperClassGetterPointer = superPointer;
        }

    }

}
