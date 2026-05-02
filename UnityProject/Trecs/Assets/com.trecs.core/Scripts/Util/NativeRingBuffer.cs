using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    public class NativeRingBuffer<T> : IDisposable, IEnumerable<T>
        where T : unmanaged
    {
        NativeArray<T> _buffer;
        int _start;
        int _end;
        int _size;

        public const int DefaultCapacity = 16;

        public NativeRingBuffer()
            : this(DefaultCapacity) { }

        public NativeRingBuffer(int initialCapacity)
        {
            if (initialCapacity <= 0)
            {
                throw new ArgumentException(
                    "Capacity must be greater than 0",
                    nameof(initialCapacity)
                );
            }

            _buffer = new NativeArray<T>(initialCapacity, Allocator.Persistent);
            _start = 0;
            _end = 0;
            _size = 0;
        }

        public IntPtr GetUnsafeReadOnlyPtr()
        {
            unsafe
            {
                return new IntPtr(_buffer.GetUnsafeReadOnlyPtr());
            }
        }

        public void Dispose()
        {
            if (_buffer.IsCreated)
            {
                _buffer.Dispose();
            }
        }

        public int Count => _size;
        public int Capacity => _buffer.Length;

        public bool IsEmpty()
        {
            return Count == 0;
        }

        public void PushBack(in T item)
        {
            if (_size == _buffer.Length)
            {
                EnsureCapacity(_buffer.Length * 2);
            }

            _buffer[_end] = item;
            _end = (_end + 1) % _buffer.Length;
            _size++;
        }

        public T PopBack()
        {
            if (_size == 0)
            {
                throw new InvalidOperationException("Buffer is empty");
            }

            _end = (_end - 1 + _buffer.Length) % _buffer.Length;
            T item = _buffer[_end];
            _buffer[_end] = default(T);
            _size--;
            return item;
        }

        public bool TryPopBack(out T result)
        {
            if (_size == 0)
            {
                result = default(T);
                return false;
            }

            result = PopBack();
            return true;
        }

        public T PopFront()
        {
            if (_size == 0)
            {
                throw new InvalidOperationException("Buffer is empty");
            }

            T item = _buffer[_start];
            _buffer[_start] = default(T);
            _start = (_start + 1) % _buffer.Length;
            _size--;
            return item;
        }

        public bool TryPopFront(out T result)
        {
            if (_size == 0)
            {
                result = default(T);
                return false;
            }

            result = _buffer[_start];
            _buffer[_start] = default(T);
            _start = (_start + 1) % _buffer.Length;
            _size--;
            return true;
        }

        public T Front()
        {
            if (_size == 0)
            {
                throw new InvalidOperationException("Buffer is empty");
            }

            return _buffer[_start];
        }

        public T this[int index]
        {
            get
            {
                if (index < 0 || index >= _size)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _buffer[(_start + index) % _buffer.Length];
            }
        }

        public void EnsureCapacity(int minCapacity)
        {
            if (minCapacity <= _buffer.Length)
            {
                return;
            }

            int newCapacity = Math.Max(minCapacity, _buffer.Length * 2);
            var newBuffer = new NativeArray<T>(newCapacity, Allocator.Persistent);

            if (_size > 0)
            {
                if (_start < _end)
                {
                    // Simple case: no wrap-around
                    NativeArray<T>.Copy(_buffer, _start, newBuffer, 0, _size);
                }
                else
                {
                    // Wrap-around case: copy in two parts
                    int firstPartSize = _buffer.Length - _start;
                    NativeArray<T>.Copy(_buffer, _start, newBuffer, 0, firstPartSize);
                    NativeArray<T>.Copy(_buffer, 0, newBuffer, firstPartSize, _end);
                }
            }

            _buffer.Dispose();
            _buffer = newBuffer;
            _start = 0;
            _end = _size;
        }

        public void Clear()
        {
            // Clear individual elements to default values
            for (int i = 0; i < _buffer.Length; i++)
            {
                _buffer[i] = default(T);
            }

            _start = 0;
            _end = 0;
            _size = 0;
        }

        public NativeRingBufferEnumerator<T> GetEnumerator()
        {
            return new NativeRingBufferEnumerator<T>(_buffer, _start, _size);
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return new NativeRingBufferIEnumerableEnumerator<T>(_buffer, _start, _size);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new NativeRingBufferIEnumerableEnumerator<T>(_buffer, _start, _size);
        }
    }

    public ref struct NativeRingBufferEnumerator<T>
        where T : unmanaged
    {
        readonly NativeArray<T> _buffer;
        readonly int _start;
        readonly int _size;
        readonly int _capacity;
        int _counter;

        public NativeRingBufferEnumerator(NativeArray<T> buffer, int start, int size)
        {
            _buffer = buffer;
            _start = start;
            _size = size;
            _capacity = buffer.Length;
            _counter = 0;
        }

        public readonly T Current => _buffer[(_start + _counter - 1) % _capacity];

        public bool MoveNext() => _counter++ < _size;

        public void Reset() => _counter = 0;
    }

    public struct NativeRingBufferIEnumerableEnumerator<T> : IEnumerator<T>
        where T : unmanaged
    {
        readonly NativeArray<T> _buffer;
        readonly int _start;
        readonly int _size;
        readonly int _capacity;
        int _counter;

        public NativeRingBufferIEnumerableEnumerator(NativeArray<T> buffer, int start, int size)
        {
            _buffer = buffer;
            _start = start;
            _size = size;
            _capacity = buffer.Length;
            _counter = 0;
        }

        public readonly T Current => _buffer[(_start + _counter - 1) % _capacity];

        readonly object IEnumerator.Current => Current;

        public bool MoveNext() => _counter++ < _size;

        public void Reset() => _counter = 0;

        public readonly void Dispose() { }
    }
}
