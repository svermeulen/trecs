using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // Does not implement IEnumerable<T> or IReadOnlyList<T> by design: foreach
    // over an interface-typed variable boxes the struct enumerator, causing a
    // GC allocation per iteration. Use this wrapper for zero-alloc readonly access.
    public readonly struct ReadOnlyList<T>
    {
        public static readonly ReadOnlyList<T> Empty = new(new List<T>(0));

        readonly List<T> _list;

        public ReadOnlyList(List<T> list)
        {
            TrecsDebugAssert.IsNotNull(list);
            _list = list;
        }

        public bool IsValid => _list != null;

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _list.Count;
        }

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _list[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public List<T>.Enumerator GetEnumerator()
        {
            return _list.GetEnumerator();
        }

        public static implicit operator ReadOnlyList<T>(List<T> list)
        {
            return new ReadOnlyList<T>(list);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T item)
        {
            return _list.Contains(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(T[] array, int arrayIndex)
        {
            _list.CopyTo(array, arrayIndex);
        }
    }
}
