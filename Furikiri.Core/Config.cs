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
    }
}