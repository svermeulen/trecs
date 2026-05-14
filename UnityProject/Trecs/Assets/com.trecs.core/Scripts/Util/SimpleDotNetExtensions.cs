using System;
using System.Collections.Generic;
using System.Linq;

namespace Trecs.Internal
{
    internal static class SimpleDotNetExtensions
    {
        public static string Join(this IEnumerable<string> values, string separator)
        {
            return string.Join(separator, values.ToArray());
        }

        public static bool DerivesFrom<T>(this Type a)
        {
            return DerivesFrom(a, typeof(T));
        }

        // This seems easier to think about than IsAssignableFrom
        public static bool DerivesFrom(this Type a, Type b)
        {
            return b != a && a.DerivesFromOrEqual(b);
        }

        public static bool DerivesFromOrEqual<T>(this Type a)
        {
            return DerivesFromOrEqual(a, typeof(T));
        }

        public static bool DerivesFromOrEqual(this Type a, Type b)
        {
#if UNITY_WSA && ENABLE_DOTNET && !UNITY_EDITOR
            return b == a || b.GetTypeInfo().IsAssignableFrom(a.GetTypeInfo());
#else
            return b == a || b.IsAssignableFrom(a);
#endif
        }

        public static bool IsEmpty<T>(this List<T> list)
        {
            return list.Count == 0;
        }

        public static bool IsEmpty<T>(this Queue<T> queue)
        {
            return queue.Count == 0;
        }

        public static bool IsEmpty<K, V>(this Dictionary<K, V> map)
        {
            return map.Count == 0;
        }

        public static bool IsEmpty<K, V>(this IReadOnlyDictionary<K, V> map)
        {
            return map.Count == 0;
        }

        public static bool IsEmpty<T>(this IReadOnlyList<T> list)
        {
            return list.Count == 0;
        }

        public static bool IsEmpty<T>(this HashSet<T> set)
        {
            return set.Count == 0;
        }

        public static bool IsEmpty<T>(this Stack<T> set)
        {
            return set.Count == 0;
        }
    }
}
