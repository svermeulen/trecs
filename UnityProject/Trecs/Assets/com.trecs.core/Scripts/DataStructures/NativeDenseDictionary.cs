using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    // Similar to NativeHashMap but allows iterating over values and key
    // in contiguous memory
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NativeDenseDictionary<TKey, TValue> : IDisposable
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        [NativeDisableContainerSafetyRestriction]
        NativeHashMap<TKey, int> _keyToIndex;

        [NativeDisableContainerSafetyRestriction]
        NativeList<TValue> _values;

        [NativeDisableContainerSafetyRestriction]
        NativeList<TKey> _keys;

        public NativeDenseDictionary(int size, Allocator allocator)
            : this()
        {
            Assert.That(size >= 0, "NativeDenseDictionary size must be non-negative");

            _keyToIndex = new NativeHashMap<TKey, int>(size, allocator);
            _values = new NativeList<TValue>(size, allocator);
            _keys = new NativeList<TKey>(size, allocator);
        }

        public readonly bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _values.IsCreated;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool IsEmpty()
        {
            return Count == 0;
        }

        internal readonly void SerializeValues(ITrecsSerializationWriter writer)
        {
            // Note that this method can be very hot
            // eg. can be called per frame in some multiplayer enrivonments
            // This is why we directly use BlitWrite / BlitRead

            int count = Count;
            writer.Write("count", count);
            if (count > 0)
            {
                unsafe
                {
                    writer.BlitWriteRawBytes(
                        "keys",
                        NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(_keys),
                        count * UnsafeUtility.SizeOf<TKey>()
                    );
                    writer.BlitWriteRawBytes(
                        "values",
                        NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(_values),
                        count * UnsafeUtility.SizeOf<TValue>()
                    );
                }
            }
        }

        internal void DeserializeValues(ITrecsSerializationReader reader)
        {
            int count = default;
            reader.Read("count", ref count);

            _keyToIndex.Clear();
            _values.Clear();
            _keys.Clear();

            if (count > 0)
            {
                EnsureCapacity(count);
                _keys.Resize(count, NativeArrayOptions.UninitializedMemory);
                _values.Resize(count, NativeArrayOptions.UninitializedMemory);

                unsafe
                {
                    reader.BlitReadRawBytes(
                        "keys",
                        NativeListUnsafeUtility.GetUnsafePtr(_keys),
                        count * UnsafeUtility.SizeOf<TKey>()
                    );
                    reader.BlitReadRawBytes(
                        "values",
                        NativeListUnsafeUtility.GetUnsafePtr(_values),
                        count * UnsafeUtility.SizeOf<TValue>()
                    );
                }

                // Rebuild hash map from keys
                for (int i = 0; i < count; i++)
                {
                    _keyToIndex.Add(_keys[i], i);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeBuffer<TValue> GetValuesRead(out int count)
        {
            count = Count;
            return new NativeBuffer<TValue>(_values);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly NativeBuffer<TValue> GetValuesWrite(out int count)
        {
            count = Count;
            return new NativeBuffer<TValue>(_values);
        }

        public readonly NativeBuffer<TKey> UnsafeKeys
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new NativeBuffer<TKey>(_keys);
        }

        public readonly NativeBuffer<TValue> UnsafeValues
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new NativeBuffer<TValue>(_values);
        }

        public readonly int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _values.Length;
        }

        public readonly KeyEnumerable Keys => new KeyEnumerable(this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly KeyValueEnumerator GetEnumerator()
        {
            return new KeyValueEnumerator(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(TKey key, in TValue value)
        {
            var itemAdded = AddValue(key, out var index);

#if TRECS_INTERNAL_CHECKS && DEBUG
            Assert.That(itemAdded, "Key {} already present", key);
#endif
            _values[index] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(TKey key, in TValue value, out int index)
        {
            var itemAdded = AddValue(key, out index);

            if (itemAdded)
                _values[index] = value;

            return itemAdded;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(TKey key, in TValue value)
        {
            var itemAdded = AddValue(key, out var index);

#if TRECS_INTERNAL_CHECKS && DEBUG
            if (itemAdded)
                throw new KeyNotFoundException("trying to set a value on a not existing key");
#endif

            _values[index] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            if (_values.Length == 0)
            {
                return;
            }

            _keyToIndex.Clear();
            _values.Clear();
            _keys.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool ContainsKey(TKey key)
        {
            return _keyToIndex.ContainsKey(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetValue(TKey key, out TValue result)
        {
            if (_keyToIndex.TryGetValue(key, out int index))
            {
                result = _values[index];
                return true;
            }

            result = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key)
        {
            if (_keyToIndex.TryGetValue(key, out int findIndex))
            {
                return ref GetRefAt(findIndex);
            }

            AddValue(key, out var newIndex);

            return ref GetRefAt(newIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key, Func<TValue> builder)
        {
            if (_keyToIndex.TryGetValue(key, out int findIndex))
            {
                return ref GetRefAt(findIndex);
            }

            AddValue(key, out var newIndex);

            _values[newIndex] = builder();

            return ref GetRefAt(newIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key, out int index)
        {
            if (_keyToIndex.TryGetValue(key, out int findIndex))
            {
                index = findIndex;
                return ref GetRefAt(findIndex);
            }

            AddValue(key, out index);

            return ref GetRefAt(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd<W>(TKey key, FuncRef<W, TValue> builder, ref W parameter)
        {
            if (_keyToIndex.TryGetValue(key, out int findIndex))
            {
                return ref GetRefAt(findIndex);
            }

            AddValue(key, out var newIndex);

            _values[newIndex] = builder(ref parameter);

            return ref GetRefAt(newIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //WARNING this method must stay stateless (not relying on states that can change, it's ok to read
        //constant states) because it will be used in multithreaded parallel code
        public ref TValue GetValueAtIndexByRef(int index)
        {
            return ref GetRefAt(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueByRef(TKey key)
        {
#if TRECS_INTERNAL_CHECKS && DEBUG
            if (_keyToIndex.TryGetValue(key, out int findIndex))
                return ref GetRefAt(findIndex);

            throw new KeyNotFoundException("Key not found");
#else
            //Burst is not able to vectorise code if throw is found, regardless if it's actually ever thrown
            _keyToIndex.TryGetValue(key, out int findIndex);

            return ref GetRefAt(findIndex);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int size)
        {
            if (_values.Capacity < size)
            {
                _values.Capacity = size;
                _keys.Capacity = size;
            }

            if (_keyToIndex.Capacity < size)
            {
                _keyToIndex.Capacity = size;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncreaseCapacityBy(int size)
        {
            var newCapacity = _values.Capacity + size;

            _values.Capacity = newCapacity;
            _keys.Capacity = newCapacity;

            if (_keyToIndex.Capacity < newCapacity)
                _keyToIndex.Capacity = newCapacity;
        }

        public TValue this[TKey key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _values[GetIndex(key)];
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                AddValue(key, out var index);

                _values[index] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(TKey key)
        {
            return Remove(key, out _, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(TKey key, out int index, out TValue value)
        {
            if (!_keyToIndex.TryGetValue(key, out int indexToRemove))
            {
                index = default;
                value = default;
                return false;
            }

            index = indexToRemove;
            value = _values[indexToRemove];

            int lastIndex = _values.Length - 1;

            _keyToIndex.Remove(key);

            if (indexToRemove != lastIndex)
            {
                TKey lastKey = _keys[lastIndex];
                _keyToIndex[lastKey] = indexToRemove;
            }

            _values.RemoveAtSwapBack(indexToRemove);
            _keys.RemoveAtSwapBack(indexToRemove);

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Trim()
        {
            if (_values.Length < _values.Capacity)
            {
                _values.Capacity = _values.Length;
                _keys.Capacity = _keys.Length;
            }

            if (_keyToIndex.Count < _keyToIndex.Capacity)
            {
                _keyToIndex.Capacity = _keyToIndex.Count;
            }
        }

        //WARNING this method must stay stateless (not relying on states that can change, it's ok to read
        //constant states) because it will be used in multithreaded parallel code
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetIndex(TKey key, out int findIndex)
        {
            if (_keyToIndex.TryGetValue(key, out int index))
            {
                findIndex = index;
                return true;
            }

            findIndex = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int GetIndex(TKey key)
        {
#if TRECS_INTERNAL_CHECKS && DEBUG
            if (_keyToIndex.TryGetValue(key, out int findIndex))
                return findIndex;

            ThrowManagedKeyNotFoundException(key);
            throw new KeyNotFoundException("Key not found in NativeDenseDictionary");
#else
            //Burst is not able to vectorise code if throw is found, regardless if it's actually ever thrown
            _keyToIndex.TryGetValue(key, out int findIndex);

            return findIndex;
#endif
        }

        [BurstDiscard]
        readonly void ThrowManagedKeyNotFoundException(TKey key)
        {
            throw Assert.CreateException(
                "Key {} not found in NativeDenseDictionary for type {}",
                key,
                typeof(TValue)
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool AddValue(TKey key, out int indexSet)
        {
            if (_keyToIndex.TryGetValue(key, out int existingIndex))
            {
                indexSet = existingIndex;
                return false;
            }

            int newIndex = _values.Length;
            _values.Add(default);
            _keys.Add(key);
            _keyToIndex.Add(key, newIndex);

            indexSet = newIndex;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref TValue GetRefAt(int index)
        {
#if TRECS_INTERNAL_CHECKS && DEBUG
            if (index < 0 || index >= _values.Length)
                throw new IndexOutOfRangeException(
                    $"NativeDenseDictionary.GetRefAt: index {index} out of range [0, {_values.Length})"
                );
#endif
            unsafe
            {
                return ref UnsafeUtility.ArrayElementAsRef<TValue>(
                    NativeListUnsafeUtility.GetUnsafePtr(_values),
                    index
                );
            }
        }

        public void Dispose()
        {
            _keyToIndex.Dispose();
            _values.Dispose();
            _keys.Dispose();
        }

        public readonly struct KeyEnumerable
        {
            readonly NativeDenseDictionary<TKey, TValue> _dic;

            public KeyEnumerable(NativeDenseDictionary<TKey, TValue> dic)
            {
                _dic = dic;
            }

            public KeyEnumerator GetEnumerator() => new(_dic);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public struct KeyEnumerator
        {
            readonly NativeDenseDictionary<TKey, TValue> _dic;
            readonly int _count;

            int _index;

            public KeyEnumerator(NativeDenseDictionary<TKey, TValue> dic)
                : this()
            {
                _dic = dic;
                _index = -1;
                _count = dic.Count;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                if (_count != _dic.Count)
                {
                    throw new TrecsException("can't modify a dictionary during its iteration");
                }
#endif
                if (_index < _count - 1)
                {
                    ++_index;
                    return true;
                }

                return false;
            }

            public readonly TKey Current => _dic._keys[_index];
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public struct KeyValueEnumerator
        {
            NativeDenseDictionary<TKey, TValue> _dic;
#if TRECS_INTERNAL_CHECKS && DEBUG
            int _startCount;
#endif
            int _count;

            int _index;

            public KeyValueEnumerator(in NativeDenseDictionary<TKey, TValue> dic)
                : this()
            {
                _dic = dic;
                _index = -1;
                _count = dic.Count;
#if TRECS_INTERNAL_CHECKS && DEBUG
                _startCount = dic.Count;
#endif
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                if (_count != _startCount)
                    throw new TrecsException("can't modify a dictionary while it is iterated");
#endif
                if (_index < _count - 1)
                {
                    ++_index;
                    return true;
                }

                return false;
            }

            public KeyValuePairFast Current => new(_dic._keys[_index], _dic._values, _index);

            public void SetRange(int startIndex, int count)
            {
                _index = startIndex - 1;
                _count = count;
#if TRECS_INTERNAL_CHECKS && DEBUG
                if (_count > _startCount)
                    throw new TrecsException("can't set a count greater than the starting one");
                _startCount = count;
#endif
            }
        }

        /// <summary>
        ///the mechanism to use arrays is fundamental to work
        /// </summary>
        [DebuggerDisplay("[{key}] - {value}")]
        public readonly struct KeyValuePairFast
        {
            readonly NativeList<TValue> _dicValues;
            readonly TKey _key;
            readonly int _index;

            public KeyValuePairFast(in TKey key, NativeList<TValue> dicValues, int index)
            {
                _dicValues = dicValues;
                _index = index;
                _key = key;
            }

            public void Deconstruct(out TKey key, out TValue value)
            {
                key = this.Key;
                value = this.Value;
            }

            public TKey Key => _key;

            public ref TValue Value
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
#if TRECS_INTERNAL_CHECKS && DEBUG
                    if (_index < 0 || _index >= _dicValues.Length)
                        throw new IndexOutOfRangeException(
                            $"KeyValuePairFast.Value: index {_index} out of range [0, {_dicValues.Length})"
                        );
#endif
                    unsafe
                    {
                        return ref UnsafeUtility.ArrayElementAsRef<TValue>(
                            NativeListUnsafeUtility.GetUnsafePtr(_dicValues),
                            _index
                        );
                    }
                }
            }
        }
    }
}
