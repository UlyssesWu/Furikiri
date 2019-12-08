using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri
{
    public class Config
    {
        public static bool AggressiveStringMerge { get; set; } = true;
        public bool HideVoidReturn { get; set; } = true;
        public bool UseBooleanWhenPossible { get; set; } = false;
    }
}