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
        /// 使用集合字面量语法初始化集合（Dictionary: %[]，Array: []）
        /// </summary>
        public static bool UseCollectionLiteralWhenPossible { get; set; } = true;

        /// <summary>
        /// 左大括号是否另起一行。默认为 true，以保持原有 Allman 风格；
        /// 设为 false 时输出为 <c>if (...) {</c>、<c>function f() {</c>。
        /// </summary>
        public static bool OpeningBraceOnNewLine { get; set; } = true;

        /// <summary>
        /// 集合字面量的建议最大行宽。长字典会在 <c>%[</c> 后换行；
        /// 小于等于 0 时禁用自动换行。
        /// </summary>
        public static int MaxOutputLineLength { get; set; } = 120;
    }
}
