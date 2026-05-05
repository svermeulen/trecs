using System;
using System.Collections;
using System.Collections.Generic;

namespace Trecs.Internal // not part of public api atm
{
    /// <summary>
    /// A growable double-ended queue (deque) backed by a circular buffer.
    /// </summary>
    /// <remarks>
    /// Index 0 is the front element. PushBack/PushFront grow capacity (doubling) when full.
    /// PopFront/PopBack and the indexer all run in O(1).
    /// </remarks>
    /// <typeparam name="T">The element type.</typeparam>
    public class RingDeque<T> : IEnumerable<T>
    {
        public const int DefaultCapacity = 16;

        T[] _buffer;
        int _front;
        int _back;
        int _count;
        int _version;

        public RingDeque()
            : this(DefaultCapacity) { }

        public RingDeque(int initialCapacity)
        {
            if (initialCapacity < 1)
            {
                throw new ArgumentException(
                    $"Capacity must be at least 1, was {initialCapacity}",
                    nameof(initialCapacity)
                );
            }

            _buffer = new T[initialCapacity];
            _front = 0;
            _back = 0;
            _count = 0;
            _version = 0;
        }

        public int Count => _count;
        public int Capacity => _buffer.Length;
        public bool IsEmpty => _count == 0;

        public void PushBack(in T item)
        {
            if (_count == _buffer.Length)
            {
                Grow(_buffer.Length * 2);
            }
            _buffer[_back] = item;
            _back++;
            if (_back == _buffer.Length)
                _back = 0;
            _count++;
            _version++;
        }

        public void PushFront(in T item)
        {
            if (_count == _buffer.Length)
            {
                Grow(_buffer.Length * 2);
            }
            if (_front == 0)
                _front = _buffer.Length;
            _front--;
            _buffer[_front] = item;
            _count++;
            _version++;
        }

        public T PopFront()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("Deque is empty");
            }
            var item = _buffer[_front];
            _buffer[_front] = default;
            _front++;
            if (_front == _buffer.Length)
                _front = 0;
            _count--;
            _version++;
            return item;
        }

        public bool TryPopFront(out T result)
        {
            if (_count == 0)
            {
                result = default;
                return false;
            }
            result = PopFront();
            return true;
        }

        public T PopBack()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("Deque is empty");
            }
            if (_back == 0)
                _back = _buffer.Length;
            _back--;
            var item = _buffer[_back];
            _buffer[_back] = default;
            _count--;
            _version++;
            return item;
        }

        public bool TryPopBack(out T result)
        {
            if (_count == 0)
            {
                result = default;
                return false;
            }
            result = PopBack();
            return true;
        }

        public T PeekFront()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("Deque is empty");
            }
            return _buffer[_front];
        }

        public bool TryPeekFront(out T result)
        {
            if (_count == 0)
            {
                result = default;
                return false;
            }
            result = _buffer[_front];
            return true;
        }

        public T PeekBack()
        {
            if (_count == 0)
            {
                throw new InvalidOperationException("Deque is empty");
            }
            var idx = _back == 0 ? _buffer.Length - 1 : _back - 1;
            return _buffer[idx];
        }

        public bool TryPeekBack(out T result)
        {
            if (_count == 0)
            {
                result = default;
                return false;
            }
            var idx = _back == 0 ? _buffer.Length - 1 : _back - 1;
            result = _buffer[idx];
            return true;
        }

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                var idx = _front + index;
                if (idx >= _buffer.Length)
                    idx -= _buffer.Length;
                return _buffer[idx];
            }
            set
            {
                if ((uint)index >= (uint)_count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }
                var idx = _front + index;
                if (idx >= _buffer.Length)
                    idx -= _buffer.Length;
                _buffer[idx] = value;
                _version++;
            }
        }

        public void EnsureCapacity(int minCapacity)
        {
            if (minCapacity <= _buffer.Length)
            {
                return;
            }
            Grow(Math.Max(minCapacity, _buffer.Length * 2));
        }

        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _front = 0;
            _back = 0;
            _count = 0;
            _version++;
        }

        void Grow(int newCapacity)
        {
            var newBuffer = new T[newCapacity];

            if (_count > 0)
            {
                if (_front < _back)
                {
                    Array.Copy(_buffer, _front, newBuffer, 0, _count);
                }
                else
                {
                    var firstPart = _buffer.Length - _front;
                    Array.Copy(_buffer, _front, newBuffer, 0, firstPart);
                    Array.Copy(_buffer, 0, newBuffer, firstPart, _back);
                }
            }

            _buffer = newBuffer;
            _front = 0;
            _back = _count;
            _version++;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new BoxedEnumerator(GetEnumerator());

        IEnumerator IEnumerable.GetEnumerator() => new BoxedEnumerator(GetEnumerator());

        public struct Enumerator
        {
            readonly RingDeque<T> _deque;
            readonly int _version;
            int _index;
            int _cursor;
            T _current;

            internal Enumerator(RingDeque<T> deque)
            {
                _deque = deque;
                _version = deque._version;
                _index = -1;
                _cursor = deque._front;
                _current = default;
            }

            public T Current => _current;

            public bool MoveNext()
            {
                if (_version != _deque._version)
                {
                    throw new InvalidOperationException(
                        "RingDeque was modified; enumeration operation may not execute."
                    );
                }
                if (_index < _deque._count - 1)
                {
                    _index++;
                    _current = _deque._buffer[_cursor];
                    _cursor++;
                    if (_cursor == _deque._buffer.Length)
                        _cursor = 0;
                    return true;
                }
                return false;
            }

            public void Reset()
            {
                if (_version != _deque._version)
                {
                    throw new InvalidOperationException(
                        "RingDeque was modified; enumeration operation may not execute."
                    );
                }
                _index = -1;
                _cursor = _deque._front;
                _current = default;
            }
        }

        sealed class BoxedEnumerator : IEnumerator<T>
        {
            Enumerator _inner;

            public BoxedEnumerator(Enumerator inner)
            {
                _inner = inner;
            }

            public T Current => _inner.Current;

            object IEnumerator.Current => _inner.Current;

            public bool MoveNext() => _inner.MoveNext();

            public void Reset() => _inner.Reset();

            public void Dispose() { }
        }
    }
}
