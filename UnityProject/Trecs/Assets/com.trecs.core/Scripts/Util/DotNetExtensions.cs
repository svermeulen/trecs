using System;
using System.Collections.Generic;
using System.Linq;

namespace Trecs.Internal
{
    internal static class DotNetExtensions
    {
        // Don't use this because Any() causes an alloc for some reason
        // public static bool IsEmpty<T>(this IEnumerable<T> enumerable)
        // {
        //     return !enumerable.Any();
        // }

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

        /// <summary>
        /// Prefer this to the normal IEnumerable Contains to avoid the alloc
        /// </summary>
        public static bool ContainsValue<T>(this IReadOnlyList<T> list, T item)
        {
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < list.Count; i++)
            {
                if (comparer.Equals(list[i], item))
                {
                    return true;
                }
            }

            return false;
        }

        // Return the first item when the list is of length one and otherwise returns default
        public static TSource OnlyOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            TrecsAssert.IsNotNull(source);

            if (source.Count() > 1)
            {
                return default(TSource);
            }

            return source.FirstOrDefault();
        }

        public static IEnumerable<T> Yield<T>(this T item)
        {
            yield return item;
        }

        public static IEnumerable<T> GetDuplicates<T>(this IEnumerable<T> list)
        {
            return list.GroupBy(x => x).Where(x => x.Skip(1).Any()).Select(x => x.Key);
        }

        public static IEnumerable<T> Except<T>(this IEnumerable<T> list, T item)
        {
            return list.Except(item.Yield());
        }

        public static string Join(this IEnumerable<string> values, string separator)
        {
            return string.Join(separator, values.ToArray());
        }

        public static void RemoveWithConfirm<T>(this IList<T> list, T item)
        {
            bool removed = list.Remove(item);
            TrecsAssert.That(removed);
        }

        /// <summary>
        /// Removes the element at the specified index by swapping it with the last element,
        /// then removing from the end. O(1) instead of O(n) but does not preserve order.
        /// </summary>
        public static void SwapRemoveAt<T>(this List<T> list, int index)
        {
            int lastIndex = list.Count - 1;
            if (index != lastIndex)
            {
                list[index] = list[lastIndex];
            }
            list.RemoveAt(lastIndex);
        }

        public static void RemoveWithConfirm<T>(this LinkedList<T> list, T item)
        {
            bool removed = list.Remove(item);
            TrecsAssert.That(removed);
        }

        public static void RemoveWithConfirm<TKey, TVal>(
            this IDictionary<TKey, TVal> dictionary,
            TKey key
        )
        {
            bool removed = dictionary.Remove(key);
            TrecsAssert.That(removed);
        }

        public static void RemoveWithConfirm<T>(this HashSet<T> set, T item)
        {
            bool removed = set.Remove(item);
            TrecsAssert.That(removed);
        }

        public static TVal GetValueAndRemove<TKey, TVal>(
            this IDictionary<TKey, TVal> dictionary,
            TKey key
        )
        {
            var removed = dictionary.Remove(key, out TVal val);
            TrecsAssert.That(removed);
            return val;
        }

        public static TVal TryGetValueAndRemove<TKey, TVal>(
            this IDictionary<TKey, TVal> dictionary,
            TKey key
        )
            where TVal : class
        {
            if (dictionary.Remove(key, out TVal val))
            {
                return val;
            }

            return null;
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

        public static string Fmt(this string s, params object[] args)
        {
            // Substitute nulls with "null" since otherwise it's just empty string
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg == null)
                {
                    args[i] = "null";
                }
            }

            return string.Format(s, args);
        }

        public static IEnumerable<(T item, int index)> Enumerate<T>(this IEnumerable<T> source)
        {
            TrecsAssert.IsNotNull(source);

            int index = 0;
            foreach (T item in source)
            {
                yield return (item, index);
                index++;
            }
        }

        public static TValue GetValueOr<TKey, TValue>(
            this IDictionary<TKey, TValue> dictionary,
            TKey key,
            TValue fallback
        )
        {
            if (dictionary.TryGetValue(key, out TValue value))
            {
                return value;
            }

            return fallback;
        }

        public static TValue TryGetValue<TKey, TValue>(
            this IDictionary<TKey, TValue> dictionary,
            TKey key
        )
            where TValue : class
        {
            TValue value;
            if (dictionary.TryGetValue(key, out value))
            {
                return value;
            }

            return null;
        }
    }
}
