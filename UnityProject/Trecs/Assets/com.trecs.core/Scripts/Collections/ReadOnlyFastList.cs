using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Trecs.Collections
{
    public readonly struct ReadOnlyFastList<T> : IEnumerable<T>
    {
        public static ReadOnlyFastList<T> DefaultEmptyList = new ReadOnlyFastList<T>(
            new FastList<T>(0)
        );

        public int Count => _list.Count;

        public ReadOnlyFastList(FastList<T> list)
        {
            _list = list;
        }

        public bool IsValid
        {
            get { return _list != null; }
        }

        public static implicit operator ReadOnlyFastList<T>(FastList<T> list)
        {
            return new ReadOnlyFastList<T>(list);
        }

        public static implicit operator LocalReadOnlyFastList<T>(ReadOnlyFastList<T> list)
        {
            return new LocalReadOnlyFastList<T>(list._list);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SvListEnumerator<T> GetEnumerator()
        {
            return _list.GetEnumerator();
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _list[index];
        }

        public ref T this[uint index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _list[(int)index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] ToArrayFast(out int count)
        {
            return _list.ToArrayFast(out count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(T[] array, int arrayIndex)
        {
            _list.CopyTo(array, arrayIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T item)
        {
            return _list.Contains(item);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return ((IEnumerable<T>)_list).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable<T>)_list).GetEnumerator();
        }

        internal readonly FastList<T> _list;
    }
}
