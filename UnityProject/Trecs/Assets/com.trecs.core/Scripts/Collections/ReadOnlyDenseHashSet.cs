using System.Collections;
using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    /// Read-only wrapper for DenseHashSet.
    /// </summary>
    public readonly struct ReadOnlyDenseHashSet<T> : IEnumerable<T>
    {
        private readonly DenseHashSet<T> _set;

        public ReadOnlyDenseHashSet(DenseHashSet<T> set)
        {
            TrecsAssert.IsNotNull(set);
            _set = set;
        }

        public static readonly ReadOnlyDenseHashSet<T> Null = default;

        public bool IsNull
        {
            get { return _set is null; }
        }

        public static implicit operator ReadOnlyDenseHashSet<T>(DenseHashSet<T> value)
        {
            return new ReadOnlyDenseHashSet<T>(value);
        }

        public int Count => _set.Count;

        public bool Contains(T item) => _set.Contains(item);

        public bool IsEmpty => _set.Count == 0;

        public DenseHashSet<T>.Enumerator GetEnumerator()
        {
            return _set.GetEnumerator();
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return ((IEnumerable<T>)_set).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)_set).GetEnumerator();
        }
    }
}
