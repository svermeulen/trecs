using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    public readonly ref struct LocalReadOnlyFastList<T>
    {
        readonly T[] _list;
        readonly int _count;

        public int Count => _count;

        public LocalReadOnlyFastList(FastList<T> list)
        {
            _list = list.ToArrayFast(out var count);
            _count = count;
        }

        public LocalReadOnlyFastList(ReadOnlyFastList<T> list)
        {
            _list = list.ToArrayFast(out var count);
            _count = count;
        }

        public LocalReadOnlyFastList(T[] list, int count)
        {
            _list = list;
            _count = count;
        }

        public static implicit operator LocalReadOnlyFastList<T>(FastList<T> list)
        {
            return new LocalReadOnlyFastList<T>(list);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public LocalFasterReadonlyListEnumerator<T> GetEnumerator()
        {
            return new LocalFasterReadonlyListEnumerator<T>(_list, Count);
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Assert.That(index >= 0 && index < _count, "Index out of range");
                return ref _list[index];
            }
        }

        public T[] ToArrayFast(out int count)
        {
            count = _count;
            return _list;
        }

        public T[] ToArray()
        {
            var array = new T[_count];
            Array.Copy(_list, 0, array, 0, _count);

            return array;
        }
    }

    public struct LocalFasterReadonlyListEnumerator<T>
    {
        readonly T[] _list;
        readonly int _count;
        int _index;

        internal LocalFasterReadonlyListEnumerator(T[] list, int count)
        {
            _list = list;
            _count = count;
            _index = -1;
        }

        public bool MoveNext()
        {
            return ++_index < _count;
        }

        public void Reset() { }

        public T Current => _list[_index];

        public void Dispose() { }
    }
}
