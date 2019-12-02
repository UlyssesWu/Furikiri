using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Furikiri.Emit
{
    /// <summary>
    /// Internal Code Object, can execute ByteCode (in krkr, not here)
    /// </summary>
    public class CodeObject : ISourceAccessor
    {
        public Module Script { get; set; }
        public string Name { get; set; }
        public TjsContextType ContextType { get; set; }
        public short[] Code { get; set; }
        public List<ITjsVariant> Variants { get; set; }

        /// <summary>
        /// Max variable count
        /// </summary>
        public int MaxVariableCount { get; set; }
        /// <summary>
        /// Variable reserve count
        /// </summary>
        public int VariableReserveCount { get; set; }
        /// <summary>
        /// Max frame count
        /// </summary>
        public int MaxFrameCount { get; set; }
        /// <summary>
        /// Func decl arg count
        /// </summary>
        public int FuncDeclArgCount { get; set; }
        /// <summary>
        /// Func decl unnamed arg array base
        /// </summary>
        public int FuncDeclUnnamedArgArrayBase { get; set; }
        /// <summary>
        /// Func decl collapse base
        /// </summary>
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

        public CodeObject(Module script, string name, int type, short[] code, List<ITjsVariant> da,
            int varCount, int varReserveCount, int maxFrame, int argCount, int arrayBase,
            int collapseBase, bool srcSorted, long[] srcPos, int[] superPointer)
        {
            Script = script;
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
        
        public string GetDisassembleSignatureString() =>
            $"({ContextType.ContextTypeName()}) {Name} 0x{GetHashCode():X8}";
    }

}
