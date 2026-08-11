namespace Furikiri
{
    public class Config
    {
        public static bool AggressiveStringMerge { get; set; } = true;
        // 调试中间结果可能很多，默认关闭；需要时可由调用方或 FURIKIRI_DEBUG_DUMP 显式开启。
        public static bool DumpDecompileDebug { get; set; } = false;
        public static bool HideVoidReturn { get; set; } = true;
        public static bool UseBooleanWhenPossible { get; set; } = false;

        /// <summary>
        /// 是否使用与 VM 寄存器槽位一致的旧变量名（如 p3、v5）。默认关闭，
        /// 参数和局部变量分别从 a0、v0 开始编号。
        /// </summary>
        public static bool UseLegacyRegisterVariableNames { get; set; } = false;

        /// <summary>
        /// 是否根据成员读取推导局部变量名。默认关闭以保持稳定的 vN；开启后
        /// 使用 name_0、name_1 等形式，并在同一函数内自动避让重名。
        /// </summary>
        public static bool UseInferredVariableNames { get; set; } = false;

        /// <summary>
        /// 使用集合字面量语法初始化集合（Dictionary: %[]，Array: []）
        /// </summary>
        public static bool UseCollectionLiteralWhenPossible { get; set; } = true;

        /// <summary>
        /// 左大括号是否另起一行。默认 false，输出为
        /// <c>if (...) {</c>、<c>function f() {</c>；设为 true 时使用 Allman 风格。
        /// </summary>
        public static bool OpeningBraceOnNewLine { get; set; } = false;

        /// <summary>
        /// 集合字面量的建议最大行宽。长字典会在 <c>%[</c> 后换行；
        /// 小于等于 0 时禁用自动换行。
        /// </summary>
        public static int MaxOutputLineLength { get; set; } = 120;
    }
}
