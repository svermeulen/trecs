using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    // When iterating over this class, note that there are two ways to do this:
    // 1. via GetEnumerator() - allows getting ref values for each item
    // 2. via IEnumerable - no ref values and also causes boxing alloc
    //
    // Advantages over normal c# list:
    // * Can get ref to items
    // * Can get underlying array for fast access
    // * No garbage during enumeration
    public sealed class FastList<T> : IEnumerable<T>
    {
        static readonly EqualityComparer<T> _comp = EqualityComparer<T>.Default;

        internal T[] _buffer;
        internal int _count;

        public FastList()
        {
            _count = 0;

            _buffer = Array.Empty<T>();
        }

        public FastList(int initialCapacity)
        {
            Assert.That(initialCapacity >= 0);

            _count = 0;
            _buffer = new T[initialCapacity];
        }

        // avoid the alloc otherwise caused by params overload
        public FastList(T value1)
        {
            _buffer = new T[1];
            _buffer[0] = value1;
            _count = 1;
        }

        // avoid the alloc otherwise caused by params overload
        public FastList(T value1, T value2)
        {
            _buffer = new T[2];
            _buffer[0] = value1;
            _buffer[1] = value2;
            _count = 2;
        }

        // avoid the alloc otherwise caused by params overload
        public FastList(T value1, T value2, T value3)
        {
            _buffer = new T[3];
            _buffer[0] = value1;
            _buffer[1] = value2;
            _buffer[2] = value3;
            _count = 3;
        }

        public FastList(params T[] collection)
        {
            Assert.IsNotNull(collection);
            _buffer = new T[collection.Length];

            Array.Copy(collection, _buffer, collection.Length);

            _count = collection.Length;
        }

        public FastList(in ArraySegment<T> collection)
        {
            _buffer = new T[collection.Count];

            collection.CopyTo(_buffer, 0);

            _count = collection.Count;
        }

        public FastList(in Span<T> collection)
        {
            _buffer = new T[collection.Length];

            collection.CopyTo(_buffer);

            _count = collection.Length;
        }

        public FastList(ICollection<T> collection)
        {
            Assert.IsNotNull(collection);
            _buffer = new T[collection.Count];

            collection.CopyTo(_buffer, 0);

            _count = collection.Count;
        }

        public FastList(ICollection<T> collection, int extraSize)
        {
            Assert.IsNotNull(collection);
            Assert.That(extraSize >= 0, "Extra size cannot be negative");
            _buffer = new T[collection.Count + extraSize];

            collection.CopyTo(_buffer, 0);

            _count = collection.Count;
        }

        public FastList(in FastList<T> source)
        {
            _buffer = new T[source.Count];

            source.CopyTo(_buffer, 0);

            _count = source.Count;
        }

        public FastList(in ReadOnlyFastList<T> source)
        {
            _buffer = new T[source.Count];

            source.CopyTo(_buffer, 0);

            _count = source.Count;
        }

        public int Count => _count;
        public int Capacity => _buffer.Length;

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Assert.That(
                    index < _count,
                    "Fasterlist - out of bound access: index {} - count {}",
                    index,
                    _count
                );
                return ref _buffer[index];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastList<T> Add(in T item)
        {
            if (_count == _buffer.Length)
            {
                AllocateMore();
            }

            _buffer[_count++] = item;

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddAt(int location, in T item)
        {
            EnsureCountIsAtLeast(location + 1);

            _buffer[location] = item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetOrCreate(int location, in Func<T> item)
        {
            EnsureCountIsAtLeast(location + 1);

            if (_comp.Equals(this[location], default))
            {
                this[location] = item();
            }

            return ref this[location];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastList<T> AddRange(in FastList<T> items)
        {
            AddRange(items._buffer, items.Count);

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastList<T> AddRange(in ReadOnlyFastList<T> items)
        {
            AddRange(items._list._buffer, items.Count);

            return this;
        }

        public void EnsureCapacity(int newCapacity)
        {
            if (_buffer.Length < newCapacity)
            {
                AllocateTo(newCapacity);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRange(T[] items, int count)
        {
            if (count == 0)
            {
                return;
            }

            if (_count + count > _buffer.Length)
            {
                AllocateMore(_count + count);
            }

            Array.Copy(items, 0, _buffer, _count, count);
            _count += count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRange(T[] items)
        {
            AddRange(items, items.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T item)
        {
            for (int index = 0; index < _count; index++)
            {
                if (_comp.Equals(_buffer[index], item))
                {
                    return true;
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FastList<T> AddRange(IEnumerable<T> items)
        {
            Assert.IsNotNull(items);

            // Pre-allocate if we know the count
            if (items is ICollection<T> collection)
            {
                EnsureCapacity(_count + collection.Count);
            }

            foreach (T item in items)
            {
                Add(item);
            }

            return this;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(T[] array, int arrayIndex)
        {
            Array.Copy(_buffer, 0, array, arrayIndex, Count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MemClear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            if (!TypeMeta<T>.IsUnmanaged)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
            }

            _count = 0;
        }

        public static FastList<T> Fill<U>(int initialSize)
            where U : T, new()
        {
            var list = PreFill<U>(initialSize);

            list._count = initialSize;

            return list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SvListEnumerator<T> GetEnumerator()
        {
            return new SvListEnumerator<T>(this, Count);
        }

        public void IncreaseCapacityBy(int increment)
        {
            IncreaseCapacityTo(_buffer.Length + increment);
        }

        public void IncreaseCapacityTo(int newCapacity)
        {
            Assert.That(newCapacity > _buffer.Length);

            var newList = new T[newCapacity];

            if (_count > 0)
            {
                Array.Copy(_buffer, newList, _count);
            }

            _buffer = newList;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCountTo(int newCount)
        {
            if (_buffer.Length < newCount)
            {
                AllocateMore(newCount);
            }

            _count = newCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCountIsAtLeast(int newCount)
        {
            if (_buffer.Length < newCount)
            {
                AllocateMore(newCount);
            }

            if (_count < newCount)
            {
                _count = newCount;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncrementCountBy(int increment)
        {
            var count = _count + increment;

            if (_buffer.Length < count)
            {
                AllocateMore(count);
            }

            _count = count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void InsertAt(int index, in T item)
        {
            Assert.That(index <= _count, "out of bound index");

            if (_count == _buffer.Length)
            {
                AllocateMore();
            }

            Array.Copy(_buffer, index, _buffer, index + 1, _count - index);
            ++_count;

            _buffer[index] = item;
        }

        public static explicit operator FastList<T>(T[] array)
        {
            return new FastList<T>(array);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly T Peek()
        {
            Assert.That(_count > 0, "Cannot peek from empty list");
            return ref _buffer[_count - 1];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly T Pop()
        {
            Assert.That(_count > 0, "Cannot pop from empty list");
            --_count;
            return ref _buffer[_count];
        }

        /// <summary>
        ///     this is a dirtish trick to be able to use the index operator
        ///     before adding the elements through the Add functions
        /// </summary>
        /// <typeparam name="U"></typeparam>
        /// <param name="initialSize"></param>
        /// <returns></returns>
        public static FastList<T> PreFill<U>(int initialSize)
            where U : T, new()
        {
            var list = new FastList<T>(initialSize);

            if (default(U) == null)
            {
                for (var i = 0; i < initialSize; i++)
                {
                    list._buffer[i] = new U();
                }
            }

            return list;
        }

        public static FastList<T> PreInit(int initialSize)
        {
            var list = new FastList<T>(initialSize) { _count = initialSize };

            return list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Push(in T item)
        {
            AddAt(_count, item);

            return _count - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveAt(int index)
        {
            Assert.That(index < _count, "out of bound index");

            if (index == --_count)
            {
                return;
            }

            Array.Copy(_buffer, index + 1, _buffer, index, _count - index);

            _buffer[_count] = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(T item)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_comp.Equals(_buffer[i], item))
                {
                    RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public int RemoveAll(T item)
        {
            int removedCount = 0;
            int writeIndex = 0;

            for (int readIndex = 0; readIndex < _count; readIndex++)
            {
                if (_comp.Equals(_buffer[readIndex], item))
                {
                    removedCount++;
                }
                else
                {
                    if (writeIndex != readIndex)
                    {
                        _buffer[writeIndex] = _buffer[readIndex];
                    }
                    writeIndex++;
                }
            }

            if (removedCount > 0)
            {
                if (!TypeMeta<T>.IsUnmanaged)
                {
                    Array.Clear(_buffer, writeIndex, removedCount);
                }
                _count = writeIndex;
            }

            return removedCount;
        }

        public bool ReuseOneSlot<U>(out U result)
            where U : T
        {
            if (_count >= _buffer.Length)
            {
                result = default;

                return false;
            }

            if (default(U) == null)
            {
                result = (U)_buffer[_count];

                if (result != null)
                {
                    _count++;
                    return true;
                }

                return false;
            }

            _count++;
            result = default;
            return true;
        }

        public bool ReuseOneSlot<U>()
            where U : T
        {
            if (_count >= _buffer.Length)
            {
                return false;
            }

            _count++;

            return true;
        }

        public bool ReuseOneSlot()
        {
            if (_count >= _buffer.Length)
            {
                return false;
            }

            _count++;

            return true;
        }

        public FastList<T> ShallowClone()
        {
            var clone = new FastList<T>(_count);
            Array.Copy(_buffer, 0, clone._buffer, 0, _count);
            clone._count = _count;
            return clone;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] ToArray()
        {
            var destinationArray = new T[_count];

            Array.Copy(_buffer, 0, destinationArray, 0, _count);

            return destinationArray;
        }

        /// <summary>
        ///     This function exists to allow fast iterations. The size of the array returned cannot be
        ///     used. The list count must be used instead.
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] ToArrayFast(out int count)
        {
            count = (int)_count;

            return _buffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Trim()
        {
            if (_count < _buffer.Length)
            {
                Array.Resize(ref _buffer, _count);
            }
            else
            {
                Assert.That(_count == _buffer.Length);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void TrimCount(int newCount)
        {
            Assert.That(_count >= newCount, "the new length must be less than the current one");

            _count = newCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool UnorderedRemoveAt(int index)
        {
            Assert.That(index < _count && _count > 0, "out of bound index");

            if (index == --_count)
            {
                _buffer[_count] = default;
                return false;
            }

            _buffer[index] = _buffer[_count];
            _buffer[_count] = default;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AllocateMore()
        {
            var newLength = (int)((_buffer.Length + 1) * 1.5f);
            var newList = new T[newLength];

            if (_count > 0)
            {
                Array.Copy(_buffer, newList, _count);
            }

            _buffer = newList;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //Note: maybe I should be sure that the count is always multiple of 4
        void AllocateMore(int newSize)
        {
            Assert.That(newSize > _buffer.Length);
            var newLength = (int)(newSize * 1.5f);

            var newList = new T[newLength];

            if (_count > 0)
            {
                Array.Copy(_buffer, newList, _count);
            }

            _buffer = newList;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AllocateTo(int newSize)
        {
            Assert.That(newSize > _buffer.Length);

            var newList = new T[newSize];

            if (_count > 0)
            {
                Array.Copy(_buffer, newList, _count);
            }

            _buffer = newList;
        }

        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return new SvListIEnumerableEnumerator<T>(this, _count);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return new SvListIEnumerableEnumerator<T>(this, _count);
        }
    }

    public ref struct SvListEnumerator<T>
    {
        readonly FastList<T> _buffer;
        readonly int _size;

        int _counter;

        public SvListEnumerator(FastList<T> buffer, int size)
        {
            _size = size;
            _counter = 0;
            _buffer = buffer;
        }

        public readonly ref T Current
        {
            get
            {
                Assert.That(_counter > 0 && _counter <= _size, "Invalid enumerator state");
                return ref _buffer[_counter - 1];
            }
        }

        public bool MoveNext()
        {
            Assert.That(
                _size == _buffer.Count,
                "SvListEnumerator: the list has been modified during the iteration"
            );

            if (_counter++ < _size)
            {
                return true;
            }

            return false;
        }

        public void Reset()
        {
            _counter = 0;
        }
    }

    public struct SvListIEnumerableEnumerator<T> : IEnumerator<T>
    {
        readonly FastList<T> _buffer;
        readonly int _size;
        int _counter;

        public SvListIEnumerableEnumerator(FastList<T> buffer, int size)
        {
            _size = size;
            _counter = 0;
            _buffer = buffer;
        }

        public readonly T Current
        {
            get
            {
                Assert.That(_counter > 0 && _counter <= _size, "Invalid enumerator state");
                return _buffer[_counter - 1];
            }
        }

        readonly object IEnumerator.Current
        {
            get
            {
                Assert.That(_counter > 0 && _counter <= _size, "Invalid enumerator state");
                return _buffer[_counter - 1];
            }
        }

        public bool MoveNext()
        {
            Assert.That(
                _size == _buffer.Count,
                "SvListEnumerator: the list has been modified during the iteration"
            );

            if (_counter++ < _size)
            {
                return true;
            }

            return false;
        }

        public void Reset()
        {
            _counter = 0;
        }

        public readonly void Dispose()
        {
            // do nothing
        }
    }
}
