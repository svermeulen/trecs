using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    /// Managed dictionary with deterministic iteration order and direct array
    /// access to values. Use when you need to iterate key-value pairs in a stable,
    /// deterministic order (required for simulation correctness in Trecs).
    /// Fastest TryGetValue of all dictionary types under IL2CPP. Slower Add/Remove
    /// than Dictionary due to dual-array bookkeeping.
    /// For lookup-only use (no iteration), prefer <see cref="Dictionary{TKey,TValue}"/>.
    /// For Burst/jobs, use <see cref="Trecs.Internal.NativeIterableDictionary{TKey,TValue}"/>.
    /// Does not implement IEnumerable by design: foreach over an interface-typed
    /// variable boxes the struct enumerator, causing a GC allocation per iteration.
    /// Use the concrete type or ReadOnlyIterableDictionary for zero-alloc foreach.
    /// </summary>
    public sealed class IterableDictionary<TKey, TValue>
        : IReadOnlyIterableDictionary<TKey, TValue>,
            IIterableDictionaryVersion<TKey>
        where TKey : struct, IEquatable<TKey>
    {
        IterableDictionaryNode<TKey>[] _valuesInfo;
        TValue[] _values;
        int[] _buckets;

        int _freeValueCellIndex;
        int _collisions;
        ulong _fastModBucketsMultiplier;
        ushort _version;

        ushort IIterableDictionaryVersion<TKey>.Version => _version;

        public IterableDictionary(int size)
        {
            TrecsDebugAssert.That(size >= 0, "IterableDictionary size must be non-negative");

            _valuesInfo = new IterableDictionaryNode<TKey>[size];
            _values = new TValue[size];
            _buckets = new int[HashHelpers.GetPrime(size)];

            if (size > 0)
            {
                _fastModBucketsMultiplier = HashHelpers.GetFastModMultiplier((uint)_buckets.Length);
            }
        }

        public IterableDictionary()
            : this(1) { }

        static IterableDictionary()
        {
#if DEBUG
            try
            {
                var keyType = typeof(TKey);
                if (
                    keyType.IsValueType
                    && !keyType.IsEnum
                    && keyType.GetMethod(
                        "GetHashCode",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly
                    ) == null
                )
                    TrecsLog.Default.Warning(
                        "{0} does not implement GetHashCode -> This will cause unwanted allocations (boxing)",
                        keyType.Name
                    );
            }
            catch (AmbiguousMatchException) { }
#endif
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

        public IterableDictionaryNode<TKey>[] UnsafeKeys
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _valuesInfo;
        }

        public TValue[] UnsafeValues
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _values;
        }

        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Count == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TValue[] UnsafeGetValues(out int count)
        {
            count = _freeValueCellIndex;

            return _values;
        }

        public int Count => _freeValueCellIndex;

        public IterableDictionaryKeyEnumerable<TKey> Keys =>
            new(_valuesInfo, _freeValueCellIndex, this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator()
        {
            return new Enumerator(this);
        }

        IterableDictionaryKeyEnumerable<TKey> IReadOnlyIterableDictionary<TKey, TValue>.Keys =>
            Keys;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(TKey key, in TValue value)
        {
            var itemAdded = AddValue(key, out var index);
            TrecsDebugAssert.That(
                itemAdded,
                "Key {0} already present in IterableDictionary<{1}, {2}>",
                key,
                typeof(TKey),
                typeof(TValue)
            );
            _values[index] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(TKey key, in TValue value, out int index)
        {
            var itemAdded = AddValue(key, out index);

            if (itemAdded)
            {
                _values[index] = value;
            }

            return itemAdded;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(TKey key, in TValue value)
        {
            var itemAdded = AddValue(key, out var index);
            TrecsDebugAssert.That(!itemAdded, "trying to set a value on a not existing key");
            _values[index] = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Recycle()
        {
            // Clear only the buckets that were actually written to, using the
            // stored hash codes. This is O(entries) instead of O(bucket capacity),
            // which is much faster when the dictionary is large but sparsely used.
            var bucketsCapacity = (uint)_buckets.Length;
            for (int i = 0; i < _freeValueCellIndex; i++)
            {
                var bucketIndex = Reduce(
                    (uint)_valuesInfo[i].HashCode,
                    bucketsCapacity,
                    _fastModBucketsMultiplier
                );
                _buckets[bucketIndex] = 0;
            }

            _freeValueCellIndex = 0;
            _collisions = 0;
            unchecked
            {
                _version++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            if (_freeValueCellIndex == 0)
            {
                return;
            }

            var count = _freeValueCellIndex;
            _freeValueCellIndex = 0;
            _collisions = 0;

            Array.Clear(_buckets, 0, _buckets.Length);
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                Array.Clear(_valuesInfo, 0, count);
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                Array.Clear(_values, 0, count);
            unchecked
            {
                _version++;
            }
        }

        // Must stay stateless — used from parallel code.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(TKey key)
        {
            return TryGetIndex(key, out _);
        }

        // Must stay stateless — used from parallel code.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(TKey key, out TValue result)
        {
            if (TryGetIndex(key, out var findIndex))
            {
                result = _values[findIndex];
                return true;
            }

            result = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key)
        {
            if (TryGetIndex(key, out var findIndex))
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            _values[findIndex] = default;

            return ref _values[findIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key, Func<TValue> builder)
        {
            if (TryGetIndex(key, out var findIndex))
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            _values[findIndex] = builder();

            return ref _values[findIndex];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key, out int index)
        {
            if (TryGetIndex(key, out index))
            {
                return ref _values[index];
            }

            AddValue(key, out index);

            _values[index] = default;

            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd<W>(TKey key, FuncRef<W, TValue> builder, ref W parameter)
        {
            if (TryGetIndex(key, out var findIndex))
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            _values[findIndex] = builder(ref parameter);

            return ref _values[findIndex];
        }

        /// <summary>
        /// Gets or recycles an existing value at the given key, or adds a new one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue RecycleOrAdd<TValueProxy>(
            TKey key,
            Func<TValueProxy> builder,
            ActionRef<TValueProxy> recycler
        )
            where TValueProxy : class, TValue
        {
            if (TryGetIndex(key, out var findIndex))
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            if (_values[findIndex] == null)
            {
                _values[findIndex] = builder();
            }
            else
            {
                recycler(ref Unsafe.As<TValue, TValueProxy>(ref _values[findIndex]));
            }

            return ref _values[findIndex];
        }

        /// <summary>
        /// Gets or recycles an existing value at the given key, or adds a new one.
        /// On fast-cleared dictionaries, reuses object values surviving from before the clear.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue RecycleOrAdd<TValueProxy, W>(
            TKey key,
            FuncRef<W, TValue> builder,
            ActionRef<TValueProxy, W> recycler,
            ref W parameter
        )
            where TValueProxy : class, TValue
        {
            if (TryGetIndex(key, out var findIndex))
            {
                return ref _values[findIndex];
            }

            AddValue(key, out findIndex);

            if (_values[findIndex] == null)
            {
                _values[findIndex] = builder(ref parameter);
            }
            else
            {
                recycler(ref Unsafe.As<TValue, TValueProxy>(ref _values[findIndex]), ref parameter);
            }

            return ref _values[findIndex];
        }

        // Must stay stateless — used from parallel code.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueAtIndexByRef(int index)
        {
            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueByRef(TKey key)
        {
#if DEBUG
            if (TryGetIndex(key, out var findIndex))
                return ref _values[findIndex];

            throw TrecsDebugAssert.CreateException("Key not found");
#else
            // Burst can't vectorize if throw is reachable
            TryGetIndex(key, out var findIndex);

            return ref _values[findIndex];
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int size)
        {
            if (_values.Length < size)
            {
                var expandPrime = HashHelpers.Expand(size);

                Array.Resize(ref _values, expandPrime);
                Array.Resize(ref _valuesInfo, expandPrime);
                unchecked
                {
                    _version++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncreaseCapacityBy(int size)
        {
            var expandPrime = HashHelpers.Expand(_values.Length + size);

            Array.Resize(ref _values, expandPrime);
            Array.Resize(ref _valuesInfo, expandPrime);
            unchecked
            {
                _version++;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(TKey key)
        {
            return TryRemove(key, out _, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(TKey key, out TValue val)
        {
            return TryRemove(key, out _, out val);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveMustExist(in TKey key)
        {
            var wasRemoved = TryRemove(key);
            TrecsDebugAssert.That(wasRemoved);
        }

        public TValue RemoveAndGet(in TKey key)
        {
            if (TryRemove(key, out _, out var value))
            {
                return value;
            }

            throw TrecsDebugAssert.CreateException("Dictionary key {0} not found", key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(TKey key, out int index, out TValue value)
        {
            int hash = key.GetHashCode();
            int bucketIndex = Reduce((uint)hash, (uint)_buckets.Length, _fastModBucketsMultiplier);

            int indexToValueToRemove = _buckets[bucketIndex] - 1;
            int itemAfterCurrentOne = -1;

            // Walk the chain to find and unlink the node.
            while (indexToValueToRemove != -1)
            {
                ref var dictionaryNode = ref _valuesInfo[indexToValueToRemove];
                if (dictionaryNode.HashCode == hash && dictionaryNode.Key.Equals(key))
                {
                    if (_buckets[bucketIndex] - 1 == indexToValueToRemove)
                    {
                        _buckets[bucketIndex] = dictionaryNode.Previous + 1;
                    }
                    else
                    {
                        TrecsDebugAssert.That(
                            itemAfterCurrentOne != -1,
                            "this should never happen"
                        );
                        _valuesInfo[itemAfterCurrentOne].Previous = dictionaryNode.Previous;
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
            _freeValueCellIndex--;
            value = _values[indexToValueToRemove];

            // Swap-remove: move the last value into the removed slot to keep the array dense.
            var lastValueCellIndex = _freeValueCellIndex;
            if (indexToValueToRemove != lastValueCellIndex)
            {
                ref var dictionaryNodeToMove = ref _valuesInfo[lastValueCellIndex];

                var movingBucketIndex = Reduce(
                    (uint)dictionaryNodeToMove.HashCode,
                    (uint)_buckets.Length,
                    _fastModBucketsMultiplier
                );

                var linkedListIterationIndex = _buckets[movingBucketIndex] - 1;

                if (linkedListIterationIndex == lastValueCellIndex)
                {
                    _buckets[movingBucketIndex] = indexToValueToRemove + 1;
                }

                while (
                    _valuesInfo[linkedListIterationIndex].Previous != -1
                    && _valuesInfo[linkedListIterationIndex].Previous != lastValueCellIndex
                )
                {
                    linkedListIterationIndex = _valuesInfo[linkedListIterationIndex].Previous;
                }

                if (_valuesInfo[linkedListIterationIndex].Previous != -1)
                {
                    _valuesInfo[linkedListIterationIndex].Previous = indexToValueToRemove;
                }

                _valuesInfo[indexToValueToRemove] = dictionaryNodeToMove;
                _values[indexToValueToRemove] = _values[lastValueCellIndex];
            }

            if (RuntimeHelpers.IsReferenceOrContainsReferences<TKey>())
                _valuesInfo[_freeValueCellIndex] = default;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TValue>())
                _values[_freeValueCellIndex] = default;
            unchecked
            {
                _version++;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Trim()
        {
            Array.Resize(ref _values, _freeValueCellIndex);
            Array.Resize(ref _valuesInfo, _freeValueCellIndex);
            unchecked
            {
                _version++;
            }
        }

        // Bucket indices are stored +1 so 0 means empty (avoids -1 initialization).
        // Must stay stateless — used from parallel code.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(TKey key, out int findIndex)
        {
            TrecsDebugAssert.That(
                _buckets.Length > 0,
                "Dictionary arrays are not correctly initialized (0 size)"
            );

            int hash = key.GetHashCode();

            int bucketIndex = Reduce((uint)hash, (uint)_buckets.Length, _fastModBucketsMultiplier);

            int valueIndex = _buckets[bucketIndex] - 1;

            // Walk the bucket chain to find the exact key (hash collisions)
            while (valueIndex != -1)
            {
                ref var dictionaryNode = ref _valuesInfo[valueIndex];
                if (dictionaryNode.HashCode == hash && dictionaryNode.Key.Equals(key))
                {
                    findIndex = valueIndex;
                    return true;
                }

                valueIndex = dictionaryNode.Previous;
            }

            findIndex = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(TKey key)
        {
#if DEBUG
            if (TryGetIndex(key, out var findIndex))
                return findIndex;

            throw TrecsDebugAssert.CreateException("Key {0} not found in IterableDictionary", key);
#else
            // Burst can't vectorize if throw is reachable
            TryGetIndex(key, out var findIndex);

            return findIndex;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Intersect<OTValue>(IterableDictionary<TKey, OTValue> otherDicKeys)
        {
            for (int i = Count - 1; i >= 0; i--)
            {
                var tKey = UnsafeKeys[i].Key;
                if (!otherDicKeys.ContainsKey(tKey))
                {
                    TryRemove(tKey);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Exclude<OTValue>(IterableDictionary<TKey, OTValue> otherDicKeys)
        {
            for (int i = Count - 1; i >= 0; i--)
            {
                var tKey = UnsafeKeys[i].Key;
                if (otherDicKeys.ContainsKey(tKey))
                    TryRemove(tKey);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Union(IterableDictionary<TKey, TValue> otherDicKeys)
        {
            foreach (var other in otherDicKeys)
            {
                this[other.Key] = other.Value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool AddValue(TKey key, out int indexSet)
        {
            int hash = key.GetHashCode();
            int bucketIndex = Reduce((uint)hash, (uint)_buckets.Length, _fastModBucketsMultiplier);

            var valueIndex = _buckets[bucketIndex] - 1;

            if (valueIndex == -1)
            {
                ResizeIfNeeded();
                _valuesInfo[_freeValueCellIndex] = new IterableDictionaryNode<TKey>(key, hash);
            }
            else
            {
                int currentValueIndex = valueIndex;
                do
                {
                    ref var dictionaryNode = ref _valuesInfo[currentValueIndex];
                    if (dictionaryNode.HashCode == hash && dictionaryNode.Key.Equals(key))
                    {
                        indexSet = currentValueIndex;
                        unchecked
                        {
                            _version++;
                        }
                        return false;
                    }

                    currentValueIndex = dictionaryNode.Previous;
                } while (currentValueIndex != -1);

                ResizeIfNeeded();

                _collisions++;
                _valuesInfo[_freeValueCellIndex] = new IterableDictionaryNode<TKey>(
                    key,
                    hash,
                    valueIndex
                );
            }

            // Bucket always points to the most recently added node in its chain.
            _buckets[bucketIndex] = _freeValueCellIndex + 1;

            indexSet = _freeValueCellIndex;
            _freeValueCellIndex++;

            // Too many collisions — rehash with more buckets
            if (_collisions > _buckets.Length)
            {
                if (_buckets.Length < 100)
                {
                    RecomputeBuckets(_collisions << 1);
                }
                else
                {
                    RecomputeBuckets(HashHelpers.Expand(_collisions));
                }
            }
            unchecked
            {
                _version++;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RecomputeBuckets(int newSize)
        {
            _buckets = new int[newSize];
            _collisions = 0;
            _fastModBucketsMultiplier = HashHelpers.GetFastModMultiplier((uint)_buckets.Length);
            var bucketsCapacity = (uint)_buckets.Length;

            var freeValueCellIndex = _freeValueCellIndex;
            for (int newValueIndex = 0; newValueIndex < freeValueCellIndex; ++newValueIndex)
            {
                ref var valueInfoNode = ref _valuesInfo[newValueIndex];
                var bucketIndex = Reduce(
                    (uint)valueInfoNode.HashCode,
                    bucketsCapacity,
                    _fastModBucketsMultiplier
                );

                int existingValueIndex = _buckets[bucketIndex] - 1;
                _buckets[bucketIndex] = newValueIndex + 1;

                if (existingValueIndex == -1)
                {
                    valueInfoNode.Previous = -1;
                }
                else
                {
                    _collisions++;
                    valueInfoNode.Previous = existingValueIndex;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ResizeIfNeeded()
        {
            if (_freeValueCellIndex == _values.Length)
            {
                var expandPrime = HashHelpers.Expand(_freeValueCellIndex);

                Array.Resize(ref _values, expandPrime);
                Array.Resize(ref _valuesInfo, expandPrime);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Reduce(uint hashcode, uint N, ulong fastModBucketsMultiplier)
        {
            if (hashcode >= N)
                return (int)HashHelpers.FastMod(hashcode, N, fastModBucketsMultiplier);

            return (int)hashcode;
        }

        public readonly struct KvPair
        {
            public KvPair(in TKey key, TValue[] dicValues, int index)
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
            public ref TValue Value => ref _dicValues[_index];

            readonly TValue[] _dicValues;
            readonly TKey _key;
            readonly int _index;
        }

        public struct Enumerator
        {
            IterableDictionary<TKey, TValue> _dic;
#if DEBUG
            int _startCount;
            ushort _expectedVersion;
#endif
            int _count;

            int _index;

            public Enumerator(in IterableDictionary<TKey, TValue> dic)
                : this()
            {
                _dic = dic;
                _index = -1;
                _count = dic.Count;
#if DEBUG
                _startCount = dic.Count;
                _expectedVersion = dic._version;
#endif
            }

            public KvPair Current => new(_dic._valuesInfo[_index].Key, _dic._values, _index);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
#if DEBUG
                TrecsDebugAssert.That(
                    _dic._version == _expectedVersion,
                    "IterableDictionary modified during iteration"
                );
#endif
                if (_index < _count - 1)
                {
                    ++_index;
                    return true;
                }

                return false;
            }

            public void SetRange(int startIndex, int count)
            {
                _index = startIndex - 1;
                _count = startIndex + count;
#if DEBUG
                TrecsDebugAssert.That(
                    _count <= _startCount,
                    "can't set a count greater than the starting one"
                );
                _startCount = _count;
                _expectedVersion = _dic._version;
#endif
            }
        }

        /// <summary>
        /// Gets the internal count of elements for serialization.
        /// </summary>
        public ref int UnsafeFreeValueCellIndex => ref _freeValueCellIndex;

        /// <summary>
        /// Gets the internal buckets array for serialization.
        /// </summary>
        public int[] UnsafeBuckets => _buckets;

        /// <summary>
        /// Gets the buckets capacity for serialization.
        /// </summary>
        public int UnsafeBucketsCapacity => _buckets.Length;

        /// <summary>
        /// Gets the internal collision counter for serialization.
        /// </summary>
        public ref int UnsafeCollisions => ref _collisions;

        /// <summary>
        /// Gets the internal fast mod multiplier for serialization.
        /// </summary>
        public ref ulong UnsafeFastModBucketsMultiplier => ref _fastModBucketsMultiplier;

        /// <summary>
        /// Ensures internal arrays have at least the specified capacity for deserialization.
        /// </summary>
        internal void UnsafeEnsureCapacityForDeserialization(int valuesCount, int bucketsCapacity)
        {
            if (valuesCount > _valuesInfo.Length)
            {
                var newCapacity = HashHelpers.Expand(valuesCount);
                _valuesInfo = new IterableDictionaryNode<TKey>[newCapacity];
                _values = new TValue[newCapacity];
            }

            // ALWAYS resize buckets to exact capacity (not just when larger)
            // This ensures _buckets.Length matches _fastModBucketsMultiplier
            if (bucketsCapacity != _buckets.Length)
            {
                _buckets = new int[bucketsCapacity];
            }
        }
    }
}
