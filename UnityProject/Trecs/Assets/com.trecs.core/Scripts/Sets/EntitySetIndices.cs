using System.Runtime.CompilerServices;
using Trecs.Internal;

// Cannot be Trecs.Internal since it is used in jobs

namespace Trecs
{
    public ref struct EntitySetIndices
    {
        readonly NativeBuffer<int> _indices;
        readonly int _count;

        public EntitySetIndices(NativeBuffer<int> indices, int count)
        {
            _indices = indices;
            _count = count;
        }

        public readonly int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

        internal readonly NativeBuffer<int> Buffer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _indices;
        }

        public readonly int this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _indices.IndexAsReadOnly(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly Enumerator GetEnumerator() => new Enumerator(_indices, _count);

        public ref struct Enumerator
        {
            readonly NativeBuffer<int> _indices;
            readonly int _count;
            int _position;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(NativeBuffer<int> indices, int count)
            {
                _indices = indices;
                _count = count;
                _position = -1;
            }

            public int Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _indices.IndexAsReadOnly(_position);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext() => ++_position < _count;
        }
    }
}
