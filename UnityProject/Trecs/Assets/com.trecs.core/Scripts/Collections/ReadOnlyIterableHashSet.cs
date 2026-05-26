using System;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Does not implement IEnumerable by design: foreach over an interface-typed
    // variable boxes the struct enumerator, causing a GC allocation per iteration.
    public readonly struct ReadOnlyIterableHashSet<T>
        where T : struct, IEquatable<T>
    {
        private readonly IterableHashSet<T> _set;

        public ReadOnlyIterableHashSet(IterableHashSet<T> set)
        {
            TrecsDebugAssert.IsNotNull(set);
            _set = set;
        }

        public static readonly ReadOnlyIterableHashSet<T> Null = default;

        public bool IsNull
        {
            get { return _set is null; }
        }

        public static implicit operator ReadOnlyIterableHashSet<T>(IterableHashSet<T> value)
        {
            return new ReadOnlyIterableHashSet<T>(value);
        }

        public int Count => _set.Count;

        public bool Contains(T item) => _set.Contains(item);

        public bool IsEmpty => _set.Count == 0;

        public IterableHashSet<T>.Enumerator GetEnumerator()
        {
            return _set.GetEnumerator();
        }
    }
}
