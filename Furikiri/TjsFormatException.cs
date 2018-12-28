using System;

namespace Furikiri
{
    public enum TjsBadFormatReason
    {
        Header,
        Version,
        Objects,
    }

    public class TjsFormatException : Exception
    {
        public TjsBadFormatReason Reason { get; set; }

        public TjsFormatException(TjsBadFormatReason reason, string info) : base(info)
        {
            Reason = reason;
        }
    }
}