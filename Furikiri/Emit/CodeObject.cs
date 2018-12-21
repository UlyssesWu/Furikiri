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
        public List<ITjsVariant> Variants { get; set; }

        public int MaxVariableCount { get; set; }
        public int VariableReserveCount { get; set; }
        public int MaxFrameCount { get; set; }
        public int FuncDeclArgCount { get; set; }
        public int FuncDeclUnnamedArgArrayBase { get; set; }
        public int FuncDeclCollapseBase { get; set; }
        public bool SourcePosArraySorted { get; set; }
        public long[] SourcePosArray { get; set; }
        public int[] SuperClassGetterPointer { get; set; }

        public CodeObject Parent { get; set; }
        public CodeObject Setter { get; set; }
        public CodeObject Getter { get; set; }
        public CodeObject SuperClass { get; set; }

        public Dictionary<string, (TjsCodeObject Property, TjsInterfaceFlag Flag)> Properties =
            new Dictionary<string, (TjsCodeObject Property, TjsInterfaceFlag Flag)>();

        public CodeObject(Module block, string name, int type, short[] code, List<ITjsVariant> da,
            int varCount, int varReserveCount, int maxFrame, int argCount, int arrayBase,
            int collapseBase, bool srcSorted, long[] srcPos, int[] superPointer)
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
            FuncDeclCollapseBase = collapseBase;
            SourcePosArraySorted = srcSorted;
            SourcePosArray = srcPos;
            SuperClassGetterPointer = superPointer;
        }

        public void SetProperty(TjsInterfaceFlag flag, string name, TjsCodeObject val, CodeObject ths)
        {
            //TODO: this need a TJS function call...
            val.This = ths;
            Properties[name] = (val, flag);
        }

        public Method ResolveMethod()
        {
            return new Method(this, Code);
        }

        public string GetDisassembleSignatureString() => 
            $"({ContextType.ContextTypeName()}) {Name} 0x{GetHashCode():X8}";
    }

}
