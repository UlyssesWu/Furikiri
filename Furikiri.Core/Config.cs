namespace Furikiri
{
    public class Config
    {
        public static bool AggressiveStringMerge { get; set; } = true;
        #if DEBUG
        public static bool DumpDecompileDebug { get; set; } = true;
        #else
        public static bool DumpDecompileDebug { get; set; } = false;
        #endif
        public static bool HideVoidReturn { get; set; } = true;
        public static bool UseBooleanWhenPossible { get; set; } = false;
        /// <summary>
        /// 使用集合字面量语法初始化集合（Dictionary: %[]，Array: []）
        /// </summary>
        public static bool UseCollectionLiteralWhenPossible { get; set; } = true;
    }
}