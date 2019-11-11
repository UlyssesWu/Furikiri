using System;
using System.Collections.Generic;
using System.Text;

namespace Furikiri.Echo
{
    internal static class DecompilerExtensions
    {
        public static HashSet<T> GetIntersection<T>(this IEnumerable<HashSet<T>> list)
        {
            HashSet<T> hs = null;

            foreach (var h in list)
            {
                if (hs == null)
                {
                    hs = new HashSet<T>(h);
                }
                else
                {
                    hs.IntersectWith(h);
                }

                if (hs.Count <= 0)
                {
                    return hs;
                }
            }

            return hs;
        }
    }
}