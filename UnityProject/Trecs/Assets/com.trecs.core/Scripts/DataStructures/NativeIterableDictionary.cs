using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Trecs.Collections
{
    /// <summary>
    /// Native dictionary with deterministic iteration order, compatible with
    /// Burst and jobs. Uses the same custom hash table algorithm as
    /// <see cref="IterableDictionary{TKey,TValue}"/> but backed by
    /// native memory. Faster iteration than NativeHashMap due to dense
    /// array layout. TryGetValue is competitive with NativeHashMap; Add/Remove are
    /// slower due to dual-array bookkeeping.
    /// Struct copies share all state (same semantics as NativeList/NativeArray).
    /// Requires manual Dispose. For managed code, prefer IterableDictionary.
    /// </summary>
    [DebuggerDisplay("Count = {Count}, IsCreated = {IsCreated}, IsEmpty = {IsEmpty}")]
    [DebuggerTypeProxy(typeof(NativeIterableDictionaryDebugView<,>))]
    public struct NativeIterableDictionary<TKey, TValue> : INativeDisposable
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        struct Data
        {
            public UnsafeList<IterableDictionaryNode<TKey>> ValuesInfo;
            public UnsafeList<TValue> Values;
            public UnsafeList<int> Buckets;
            public int Collisions;
            public ulong FastModBucketsMultiplier;
        }

        unsafe Data* _data;
        readonly AllocatorManager.AllocatorHandle _allocator;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        AtomicSafetyHandle m_Safety;
#endif

        public NativeIterableDictionary(int size, AllocatorManager.AllocatorHandle allocator)
            : this()
        {
            TrecsDebugAssert.That(size >= 0, "NativeIterableDictionary size must be non-negative");

            _allocator = allocator;
            var alloc = allocator.ToAllocator;
            int bucketCount = HashHelpers.GetPrime(size);

            unsafe
            {
                _data = (Data*)
                    UnsafeUtility.Malloc(
                        UnsafeUtility.SizeOf<Data>(),
                        UnsafeUtility.AlignOf<Data>(),
                        alloc
                    );

                _data->ValuesInfo = new UnsafeList<IterableDictionaryNode<TKey>>(size, alloc);
                _data->Values = new UnsafeList<TValue>(size, alloc);
                _data->Buckets = new UnsafeList<int>(bucketCount, alloc);
                _data->Buckets.Resize(bucketCount, NativeArrayOptions.ClearMemory);
                _data->Collisions = 0;
                _data->FastModBucketsMultiplier =
                    bucketCount > 0 ? HashHelpers.GetFastModMultiplier((uint)bucketCount) : 0;
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_Safety = AtomicSafetyHandle.Create();
#endif
        }

        public readonly bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                unsafe
                {
                    return _data != null;
                }
            }
        }

        public readonly bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Count == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckRead()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly void CheckWrite()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
        }

        internal readonly void SerializeValues(ISerializationWriter writer)
        {
            CheckRead();
            int count = Count;
            writer.Write("Count", count);
            if (count > 0)
            {
                unsafe
                {
                    var tempKeys = (TKey*)
                        UnsafeUtility.Malloc(
                            count * UnsafeUtility.SizeOf<TKey>(),
                            UnsafeUtility.AlignOf<TKey>(),
                            Allocator.Temp
                        );
                    for (int i = 0; i < count; i++)
                        tempKeys[i] = _data->ValuesInfo[i].Key;

                    writer.BlitWriteRawBytes(
                        "Keys",
                        tempKeys,
                        count * UnsafeUtility.SizeOf<TKey>()
                    );
                    writer.BlitWriteRawBytes(
                        "Values",
                        _data->Values.Ptr,
                        count * UnsafeUtility.SizeOf<TValue>()
                    );

                    UnsafeUtility.Free(tempKeys, Allocator.Temp);
                }
            }
        }

        internal void DeserializeValues(ISerializationReader reader)
        {
            CheckWrite();
            int count = default;
            reader.Read("Count", ref count);

            Clear();

            if (count > 0)
            {
                EnsureCapacity(count);
                unsafe
                {
                    _data->Values.Resize(count, NativeArrayOptions.UninitializedMemory);
                    _data->ValuesInfo.Resize(count, NativeArrayOptions.UninitializedMemory);

                    var tempKeys = new NativeArray<TKey>(
                        count,
                        Allocator.Temp,
                        NativeArrayOptions.UninitializedMemory
                    );
                    reader.BlitReadRawBytes(
                        "Keys",
                        NativeArrayUnsafeUtility.GetUnsafePtr(tempKeys),
                        count * UnsafeUtility.SizeOf<TKey>()
                    );
                    reader.BlitReadRawBytes(
                        "Values",
                        _data->Values.Ptr,
                        count * UnsafeUtility.SizeOf<TValue>()
                    );

                    var bucketsCapacity = (uint)_data->Buckets.Length;
                    var fmm = _data->FastModBucketsMultiplier;

                    for (int i = 0; i < count; i++)
                    {
                        var key = tempKeys[i];
                        int hash = key.GetHashCode();
                        int bucketIndex = Reduce((uint)hash, bucketsCapacity, fmm);

                        int existingValueIndex = _data->Buckets[bucketIndex] - 1;
                        _data->ValuesInfo[i] =
                            existingValueIndex == -1
                                ? new IterableDictionaryNode<TKey>(key, hash)
                                : new IterableDictionaryNode<TKey>(key, hash, existingValueIndex);

                        if (existingValueIndex != -1)
                            _data->Collisions++;

                        _data->Buckets[bucketIndex] = i + 1;
                    }

                    tempKeys.Dispose();
                }
            }
        }

        public readonly NativeBuffer<TValue> UnsafeValues
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckRead();
                unsafe
                {
                    return new NativeBuffer<TValue>(_data->Values.Ptr, _data->Values.Length);
                }
            }
        }

        public readonly int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                unsafe
                {
                    return _data->Values.Length;
                }
            }
        }

        public readonly KeyEnumerable Keys
        {
            get
            {
                CheckRead();
                return new KeyEnumerable(this);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly KeyValueEnumerator GetEnumerator()
        {
            CheckRead();
            return new KeyValueEnumerator(this);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(TKey key, in TValue value)
        {
            CheckWrite();
            var itemAdded = AddValue(key, out var index);

#if TRECS_INTERNAL_CHECKS && DEBUG
            TrecsDebugAssert.That(itemAdded, "Key {0} already present", key);
#endif
            unsafe
            {
                _data->Values[index] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(TKey key, in TValue value, out int index)
        {
            CheckWrite();
            var itemAdded = AddValue(key, out index);

            if (itemAdded)
                unsafe
                {
                    _data->Values[index] = value;
                }

            return itemAdded;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(TKey key, in TValue value)
        {
            CheckWrite();

#if TRECS_INTERNAL_CHECKS && DEBUG
            if (!TryGetIndexInternal(key, out var index))
            {
                ThrowManagedKeyNotFoundException(key);
                throw new KeyNotFoundException("Key not found in NativeIterableDictionary");
            }
#else
            TryGetIndexInternal(key, out var index);
#endif

            unsafe
            {
                _data->Values[index] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            CheckWrite();
            unsafe
            {
                if (_data->Values.Length == 0)
                    return;

                _data->Values.Clear();
                _data->ValuesInfo.Clear();
                _data->Collisions = 0;
                UnsafeUtility.MemClear(_data->Buckets.Ptr, _data->Buckets.Length * sizeof(int));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Recycle()
        {
            CheckWrite();
            unsafe
            {
                if (_data->Values.Length == 0)
                    return;

                var bucketsCapacity = (uint)_data->Buckets.Length;
                var fmm = _data->FastModBucketsMultiplier;
                int count = _data->Values.Length;

                for (int i = 0; i < count; i++)
                {
                    var bucketIndex = Reduce(
                        (uint)_data->ValuesInfo[i].HashCode,
                        bucketsCapacity,
                        fmm
                    );
                    _data->Buckets[bucketIndex] = 0;
                }

                _data->Collisions = 0;
                _data->Values.Clear();
                _data->ValuesInfo.Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool ContainsKey(TKey key)
        {
            CheckRead();
            return TryGetIndexInternal(key, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetValue(TKey key, out TValue result)
        {
            CheckRead();
            if (TryGetIndexInternal(key, out var findIndex))
            {
                unsafe
                {
                    result = _data->Values[findIndex];
                }
                return true;
            }

            result = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key)
        {
            CheckWrite();
            if (TryGetIndexInternal(key, out var findIndex))
                return ref GetRefAt(findIndex);

            AddValue(key, out findIndex);
            return ref GetRefAt(findIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key, Func<TValue> builder)
        {
            CheckWrite();
            if (TryGetIndexInternal(key, out var findIndex))
                return ref GetRefAt(findIndex);

            AddValue(key, out findIndex);
            unsafe
            {
                _data->Values[findIndex] = builder();
            }
            return ref GetRefAt(findIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key, out int index)
        {
            CheckWrite();
            if (TryGetIndexInternal(key, out index))
                return ref GetRefAt(index);

            AddValue(key, out index);
            return ref GetRefAt(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd<W>(TKey key, FuncRef<W, TValue> builder, ref W parameter)
        {
            CheckWrite();
            if (TryGetIndexInternal(key, out var findIndex))
                return ref GetRefAt(findIndex);

            AddValue(key, out findIndex);
            unsafe
            {
                _data->Values[findIndex] = builder(ref parameter);
            }
            return ref GetRefAt(findIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueAtIndexByRef(int index)
        {
            CheckWrite();
            return ref GetRefAt(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueByRef(TKey key)
        {
#if TRECS_INTERNAL_CHECKS && DEBUG
            CheckWrite();
            if (TryGetIndexInternal(key, out var findIndex))
                return ref GetRefAt(findIndex);

            ThrowManagedKeyNotFoundException(key);
            throw new KeyNotFoundException("Key not found in NativeIterableDictionary");
#else
            CheckWrite();
            TryGetIndexInternal(key, out var findIndex);
            return ref GetRefAt(findIndex);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int size)
        {
            CheckWrite();
            unsafe
            {
                if (_data->Values.Capacity < size)
                {
                    _data->Values.Capacity = size;
                    _data->ValuesInfo.Capacity = size;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncreaseCapacityBy(int size)
        {
            CheckWrite();
            unsafe
            {
                var newCapacity = _data->Values.Capacity + size;
                _data->Values.Capacity = newCapacity;
                _data->ValuesInfo.Capacity = newCapacity;
            }
        }

        public TValue this[TKey key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckRead();
                unsafe
                {
                    return _data->Values[GetIndex(key)];
                }
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                CheckWrite();
                AddValue(key, out var index);
                unsafe
                {
                    _data->Values[index] = value;
                }
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
            CheckWrite();
            int hash = key.GetHashCode();
            unsafe
            {
                var bucketsLength = (uint)_data->Buckets.Length;
                int bucketIndex = Reduce(
                    (uint)hash,
                    bucketsLength,
                    _data->FastModBucketsMultiplier
                );

                int indexToValueToRemove = _data->Buckets[bucketIndex] - 1;
                int itemAfterCurrentOne = -1;

                while (indexToValueToRemove != -1)
                {
                    ref var dictionaryNode = ref _data->ValuesInfo.ElementAt(indexToValueToRemove);
                    if (dictionaryNode.HashCode == hash && dictionaryNode.Key.Equals(key))
                    {
                        if (_data->Buckets[bucketIndex] - 1 == indexToValueToRemove)
                        {
                            _data->Buckets[bucketIndex] = dictionaryNode.Previous + 1;
                        }
                        else
                        {
                            _data->ValuesInfo.ElementAt(itemAfterCurrentOne).Previous =
                                dictionaryNode.Previous;
                        }

                        break;
                    }

                    itemAfterCurrentOne = indexToValueToRemove;
                    indexToValueToRemove = dictionaryNode.Previous;
                }

                if (indexToValueToRemove == -1)
                {
                    index = default;
                    value = default;
                    return false;
                }

                index = indexToValueToRemove;
                value = _data->Values[indexToValueToRemove];

                var lastValueCellIndex = _data->Values.Length - 1;
                if (indexToValueToRemove != lastValueCellIndex)
                {
                    ref var dictionaryNodeToMove = ref _data->ValuesInfo.ElementAt(
                        lastValueCellIndex
                    );

                    var movingBucketIndex = Reduce(
                        (uint)dictionaryNodeToMove.HashCode,
                        bucketsLength,
                        _data->FastModBucketsMultiplier
                    );

                    var linkedListIterationIndex = _data->Buckets[movingBucketIndex] - 1;

                    if (linkedListIterationIndex == lastValueCellIndex)
                    {
                        _data->Buckets[movingBucketIndex] = indexToValueToRemove + 1;
                    }

                    while (
                        _data->ValuesInfo.ElementAt(linkedListIterationIndex).Previous != -1
                        && _data->ValuesInfo.ElementAt(linkedListIterationIndex).Previous
                            != lastValueCellIndex
                    )
                    {
                        linkedListIterationIndex = _data
                            ->ValuesInfo.ElementAt(linkedListIterationIndex)
                            .Previous;
                    }

                    if (_data->ValuesInfo.ElementAt(linkedListIterationIndex).Previous != -1)
                    {
                        _data->ValuesInfo.ElementAt(linkedListIterationIndex).Previous =
                            indexToValueToRemove;
                    }

                    _data->ValuesInfo[indexToValueToRemove] = dictionaryNodeToMove;
                    _data->Values[indexToValueToRemove] = _data->Values[lastValueCellIndex];
                }

                _data->Values.Length--;
                _data->ValuesInfo.Length--;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Trim()
        {
            CheckWrite();
            unsafe
            {
                if (_data->Values.Length < _data->Values.Capacity)
                {
                    _data->Values.Capacity = _data->Values.Length;
                    _data->ValuesInfo.Capacity = _data->Values.Length;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool TryGetIndex(TKey key, out int findIndex)
        {
            CheckRead();
            return TryGetIndexInternal(key, out findIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        readonly bool TryGetIndexInternal(TKey key, out int findIndex)
        {
            unsafe
            {
                int hash = key.GetHashCode();
                int bucketIndex = Reduce(
                    (uint)hash,
                    (uint)_data->Buckets.Length,
                    _data->FastModBucketsMultiplier
                );
                int valueIndex = _data->Buckets[bucketIndex] - 1;

                while (valueIndex != -1)
                {
                    ref var dictionaryNode = ref _data->ValuesInfo.ElementAt(valueIndex);
                    if (dictionaryNode.HashCode == hash && dictionaryNode.Key.Equals(key))
                    {
                        findIndex = valueIndex;
                        return true;
                    }

                    valueIndex = dictionaryNode.Previous;
                }
            }

            findIndex = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int GetIndex(TKey key)
        {
#if TRECS_INTERNAL_CHECKS && DEBUG
            if (TryGetIndexInternal(key, out var findIndex))
                return findIndex;

            ThrowManagedKeyNotFoundException(key);
            throw new KeyNotFoundException("Key not found in NativeIterableDictionary");
#else
            TryGetIndexInternal(key, out var findIndex);
            return findIndex;
#endif
        }

        [BurstDiscard]
        readonly void ThrowManagedKeyNotFoundException(TKey key)
        {
            throw TrecsDebugAssert.CreateException(
                "Key {0} not found in NativeIterableDictionary for type {1}",
                key,
                typeof(TValue)
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool AddValue(TKey key, out int indexSet)
        {
            unsafe
            {
                int hash = key.GetHashCode();
                int bucketIndex = Reduce(
                    (uint)hash,
                    (uint)_data->Buckets.Length,
                    _data->FastModBucketsMultiplier
                );

                var valueIndex = _data->Buckets[bucketIndex] - 1;

                if (valueIndex == -1)
                {
                    _data->ValuesInfo.Add(new IterableDictionaryNode<TKey>(key, hash));
                    _data->Values.Add(default);
                }
                else
                {
                    int currentValueIndex = valueIndex;
                    do
                    {
                        ref var dictionaryNode = ref _data->ValuesInfo.ElementAt(currentValueIndex);
                        if (dictionaryNode.HashCode == hash && dictionaryNode.Key.Equals(key))
                        {
                            indexSet = currentValueIndex;
                            return false;
                        }

                        currentValueIndex = dictionaryNode.Previous;
                    } while (currentValueIndex != -1);

                    _data->Collisions++;
                    _data->ValuesInfo.Add(new IterableDictionaryNode<TKey>(key, hash, valueIndex));
                    _data->Values.Add(default);
                }

                indexSet = _data->Values.Length - 1;
                _data->Buckets[bucketIndex] = indexSet + 1;

                if (_data->Collisions > _data->Buckets.Length)
                {
                    RecomputeBuckets(
                        _data->Buckets.Length < 100
                            ? _data->Collisions << 1
                            : HashHelpers.Expand(_data->Collisions)
                    );
                }
            }

            return true;
        }

        void RecomputeBuckets(int newSize)
        {
            unsafe
            {
                _data->Buckets.Resize(newSize, NativeArrayOptions.UninitializedMemory);
                UnsafeUtility.MemClear(_data->Buckets.Ptr, newSize * sizeof(int));

                _data->Collisions = 0;
                _data->FastModBucketsMultiplier = HashHelpers.GetFastModMultiplier((uint)newSize);
                var bucketsCapacity = (uint)newSize;

                for (int newValueIndex = 0; newValueIndex < _data->Values.Length; ++newValueIndex)
                {
                    ref var valueInfoNode = ref _data->ValuesInfo.ElementAt(newValueIndex);
                    var bi = Reduce(
                        (uint)valueInfoNode.HashCode,
                        bucketsCapacity,
                        _data->FastModBucketsMultiplier
                    );

                    int existingValueIndex = _data->Buckets[bi] - 1;
                    _data->Buckets[bi] = newValueIndex + 1;

                    if (existingValueIndex == -1)
                    {
                        valueInfoNode.Previous = -1;
                    }
                    else
                    {
                        _data->Collisions++;
                        valueInfoNode.Previous = existingValueIndex;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Reduce(uint hashcode, uint N, ulong fastModBucketsMultiplier)
        {
            if (hashcode >= N)
                return (int)HashHelpers.FastMod(hashcode, N, fastModBucketsMultiplier);

            return (int)hashcode;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ref TValue GetRefAt(int index)
        {
            unsafe
            {
#if TRECS_INTERNAL_CHECKS && DEBUG
                TrecsAssert.That(
                    index >= 0 && index < _data->Values.Length,
                    "NativeIterableDictionary.GetRefAt: index {0} out of range [0, {1})",
                    index,
                    _data->Values.Length
                );
#endif
                return ref _data->Values.ElementAt(index);
            }
        }

        public void Dispose()
        {
            unsafe
            {
                _data->ValuesInfo.Dispose();
                _data->Values.Dispose();
                _data->Buckets.Dispose();
                UnsafeUtility.Free(_data, _allocator.ToAllocator);
                _data = null;
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(m_Safety);
#endif
        }

        public JobHandle Dispose(JobHandle inputDeps)
        {
            unsafe
            {
                var deps = JobHandle.CombineDependencies(
                    _data->ValuesInfo.Dispose(inputDeps),
                    _data->Values.Dispose(inputDeps),
                    _data->Buckets.Dispose(inputDeps)
                );
                UnsafeUtility.Free(_data, _allocator.ToAllocator);
                _data = null;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.Release(m_Safety);
#endif
                return deps;
            }
        }

        public readonly struct KeyEnumerable
        {
            readonly NativeIterableDictionary<TKey, TValue> _dic;

            public KeyEnumerable(NativeIterableDictionary<TKey, TValue> dic)
            {
                _dic = dic;
            }

            public KeyEnumerator GetEnumerator() => new(_dic);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public struct KeyEnumerator
        {
            readonly NativeIterableDictionary<TKey, TValue> _dic;
            readonly int _count;
            int _index;

            public KeyEnumerator(NativeIterableDictionary<TKey, TValue> dic)
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
                    throw new TrecsException("can't modify a dictionary during its iteration");
#endif
                if (_index < _count - 1)
                {
                    ++_index;
                    return true;
                }

                return false;
            }

            public readonly TKey Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    unsafe
                    {
                        return _dic._data->ValuesInfo[_index].Key;
                    }
                }
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public struct KeyValueEnumerator
        {
            NativeIterableDictionary<TKey, TValue> _dic;
#if TRECS_INTERNAL_CHECKS && DEBUG
            int _startCount;
#endif
            int _count;
            int _index;

            public KeyValueEnumerator(in NativeIterableDictionary<TKey, TValue> dic)
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

            public KeyValuePairFast Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    unsafe
                    {
                        return new(
                            _dic._data->ValuesInfo[_index].Key,
                            _dic._data->Values.Ptr,
                            _index
                        );
                    }
                }
            }

            public void SetRange(int startIndex, int count)
            {
                _index = startIndex - 1;
                _count = startIndex + count;
#if TRECS_INTERNAL_CHECKS && DEBUG
                if (_count > _startCount)
                    throw new TrecsException("can't set a count greater than the starting one");
                _startCount = _count;
#endif
            }
        }

        [DebuggerDisplay("[{_key}] - {Value}")]
        public readonly unsafe struct KeyValuePairFast
        {
            [NativeDisableUnsafePtrRestriction]
            readonly TValue* _valuesPtr;
            readonly TKey _key;
            readonly int _index;

            public KeyValuePairFast(in TKey key, TValue* valuesPtr, int index)
            {
                _valuesPtr = valuesPtr;
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
                get => ref _valuesPtr[_index];
            }
        }
    }

    internal sealed class NativeIterableDictionaryDebugView<TKey, TValue>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        readonly NativeIterableDictionary<TKey, TValue> _data;

        public NativeIterableDictionaryDebugView(NativeIterableDictionary<TKey, TValue> data)
        {
            _data = data;
        }

        public KeyValuePair<TKey, TValue>[] Items
        {
            get
            {
                if (!_data.IsCreated)
                    return Array.Empty<KeyValuePair<TKey, TValue>>();

                var count = _data.Count;
                var result = new KeyValuePair<TKey, TValue>[count];
                int i = 0;
                foreach (var (key, value) in _data)
                {
                    result[i++] = new KeyValuePair<TKey, TValue>(key, value);
                }
                return result;
            }
        }
    }
}
