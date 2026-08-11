using System;

namespace Furikiri.Emit
{
    public class Variable
    {
        public short Slot { get; set; }
        public string Name { get; set; }
        public TjsVarType VarType { get; set; }
        public bool IsParameter { get; set; }
        public bool IsUnnamedArray { get; set; }
        public bool IsNamedArray { get; set; }

        /// <summary>
        /// 在参数区或局部变量区内的零基序号。它与 VM 的绝对槽位分离，
        /// 使输出名称不会因 this、this proxy 和参数占用的寄存器而出现跳号。
        /// </summary>
        public int? GeneratedIndex { get; set; }

        public string DefaultName
        {
            get
            {
                if (IsNamedArray)
                {
                    var suffix = Config.UseLegacyRegisterVariableNames
                        ? Math.Abs(Slot)
                        : GeneratedIndex ?? Math.Abs(Slot);
                    return $"{Const.DefaultFunctionArgArrayName}{suffix}";
                }

                if (IsUnnamedArray)
                {
                    return "*";
                }
                if (Config.UseLegacyRegisterVariableNames)
                {
                    return $"{(IsParameter ? "p" : "v")}{Math.Abs(Slot)}";
                }

                var index = GeneratedIndex ?? Math.Abs(Slot);
                return $"{(IsParameter ? "a" : "v")}{index}";
            }
        }

        public Variable(short slot)
        {
            Slot = slot;
        }

        public Variable(short slot, CodeObject obj)
        {
            Slot = slot;
            IsParameter = CheckIsParameter(obj, slot);
            if (IsParameter)
            {
                GeneratedIndex = Const.ArgBase - slot;
            }
            else if (slot <= Const.ArgBase)
            {
                // 命名折叠参数（args*）在 VM 参数区额外占一个槽，但它不属于
                // 局部变量序列；计算 vN 时必须一并越过。
                var parameterSlotCount = obj.FuncDeclArgCount +
                                         (obj.FuncDeclCollapseBase >= 0 ? 1 : 0);
                GeneratedIndex = Const.ArgBase - parameterSlotCount - slot;
            }
        }

        public static bool CheckIsParameter(CodeObject obj, short slot)
        {
            /* Register Stack
             *  1+ : intermediate slot
             *  0  : not sure
             * -1  : this
             * -2  : this proxy
             * -3- : parameter
             * -n- : variable
             */

            var argCount = obj.FuncDeclArgCount;
            var varCount = obj.MaxVariableCount;
            if (slot >= -2)
            {
                return false;
            }

            if (slot >= -2 - argCount)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public override string ToString()
        {
            // 新样式有意保持稳定的 aN/vN 命名；旧模式仍保留已有的成员名推导。
            return Config.UseLegacyRegisterVariableNames ? Name ?? DefaultName : DefaultName;
        }
    }
}
