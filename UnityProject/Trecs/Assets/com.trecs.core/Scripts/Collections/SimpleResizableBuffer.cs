using System;
using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    public class SimpleResizableBuffer<T>
    {
        internal T[] _realBuffer;
        internal bool _hasAllocated;

        public SimpleResizableBuffer(uint size)
        {
            Alloc(size);
        }

        public bool IsValid => _hasAllocated;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Alloc(uint size)
        {
            _realBuffer = new T[size];
            _hasAllocated = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resize(uint newSize, bool copyContent = true)
        {
            if (newSize != Capacity)
            {
                var realBuffer = _realBuffer;

                if (copyContent == true)
                {
                    Array.Resize(ref realBuffer, (int)newSize);
                }
                else
                {
                    realBuffer = new T[newSize];
                }

                _realBuffer = realBuffer;
                _hasAllocated = true;
            }
        }

        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _realBuffer.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            Array.Clear(_realBuffer, 0, _realBuffer.Length);
        }

        public ref T this[uint index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _realBuffer[index];
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _realBuffer[index];
        }
    }
}
