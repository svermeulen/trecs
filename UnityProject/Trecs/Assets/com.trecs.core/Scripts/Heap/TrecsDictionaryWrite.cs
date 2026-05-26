using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    public unsafe ref struct TrecsDictionaryWrite<TKey, TValue>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        readonly TrecsDictionaryHeader* _header;
        IterableDictionaryNode<TKey>* _nodes;
        TValue* _values;
        int* _buckets;
        readonly NativeHeap _store;
        ushort _capturedVersion;
        readonly NativeHeapEntry* _headerSlot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        internal TrecsDictionaryWrite(
            TrecsDictionaryHeader* header,
            IterableDictionaryNode<TKey>* nodes,
            TValue* values,
            int* buckets,
            NativeHeap store,
            NativeHeapEntry* headerSlot,
            byte capturedGeneration,
            AtomicSafetyHandle safety
        )
        {
            _header = header;
            _nodes = nodes;
            _values = values;
            _buckets = buckets;
            _store = store;
            _capturedVersion = header->Version;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        internal TrecsDictionaryWrite(
            TrecsDictionaryHeader* header,
            IterableDictionaryNode<TKey>* nodes,
            TValue* values,
            int* buckets,
            NativeHeap store,
            NativeHeapEntry* headerSlot,
            byte capturedGeneration
        )
        {
            _header = header;
            _nodes = nodes;
            _values = values;
            _buckets = buckets;
            _store = store;
            _capturedVersion = header->Version;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckSlotAlive()
        {
            TrecsAssert.That(
                _headerSlot->Generation == _capturedGeneration && _headerSlot->InUse == 1,
                "TrecsDictionaryWrite is stale: the underlying TrecsDictionary allocation "
                    + "has been freed since this wrapper was opened (captured slot gen {0}, "
                    + "current {1}, InUse {2}).",
                _capturedGeneration,
                _headerSlot->Generation,
                _headerSlot->InUse
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckVersion()
        {
            TrecsAssert.That(
                _header->Version == _capturedVersion,
                "TrecsDictionaryWrite is stale: the dictionary was mutated through "
                    + "another path since this wrapper was opened (captured version {0}, "
                    + "current {1}). Re-open the wrapper after the mutation.",
                _capturedVersion,
                _header->Version
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckReadAccess()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            CheckVersion();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckWriteAccess()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            CheckSlotAlive();
            CheckVersion();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void BumpVersionAndResync()
        {
            unchecked
            {
                _capturedVersion = ++_header->Version;
            }
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckReadAccess();
                return _header->Count;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(TKey key)
        {
            CheckReadAccess();
            return TryGetIndexInternal(key, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(TKey key, out TValue value)
        {
            CheckReadAccess();
            if (TryGetIndexInternal(key, out var index))
            {
                value = _values[index];
                return true;
            }
            value = default;
            return false;
        }

        public TValue this[TKey key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckReadAccess();
                TrecsAssert.That(
                    TryGetIndexInternal(key, out var index),
                    "Key not found in TrecsDictionary"
                );
                return _values[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                CheckWriteAccess();
                AddValue(key, out var index);
                _values[index] = value;
                BumpVersionAndResync();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(TKey key, in TValue value)
        {
            CheckWriteAccess();
            var added = AddValue(key, out var index);
            TrecsDebugAssert.That(added, "Key already present in TrecsDictionary");
            _values[index] = value;
            BumpVersionAndResync();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(TKey key, in TValue value)
        {
            CheckWriteAccess();
            var added = AddValue(key, out var index);
            if (added)
            {
                _values[index] = value;
                BumpVersionAndResync();
            }
            return added;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(TKey key, in TValue value)
        {
            CheckWriteAccess();
            TrecsAssert.That(
                TryGetIndexInternal(key, out var index),
                "Key not found in TrecsDictionary"
            );
            _values[index] = value;
            BumpVersionAndResync();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key)
        {
            CheckWriteAccess();
            if (AddValue(key, out var index))
                BumpVersionAndResync();
            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueByRef(TKey key)
        {
            CheckWriteAccess();
            TrecsAssert.That(
                TryGetIndexInternal(key, out var index),
                "Key not found in TrecsDictionary"
            );
            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueAtIndex(int index)
        {
            CheckWriteAccess();
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "TrecsDictionaryWrite.GetValueAtIndex index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TKey GetKeyAtIndex(int index)
        {
            CheckReadAccess();
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "TrecsDictionaryWrite.GetKeyAtIndex index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            return _nodes[index].Key;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(TKey key, out int findIndex)
        {
            CheckReadAccess();
            return TryGetIndexInternal(key, out findIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(TKey key)
        {
            return Remove(key, out _);
        }

        public bool Remove(TKey key, out TValue removedValue)
        {
            CheckWriteAccess();

            if (_header->BucketCount == 0)
            {
                removedValue = default;
                return false;
            }

            int hash = key.GetHashCode();
            var bucketsLength = (uint)_header->BucketCount;
            int bucketIndex = TrecsDictionary.Reduce(
                (uint)hash,
                bucketsLength,
                _header->FastModMultiplier
            );

            int indexToRemove = _buckets[bucketIndex] - 1;
            int itemAfterCurrent = -1;

            while (indexToRemove != -1)
            {
                ref var node = ref _nodes[indexToRemove];
                if (node.HashCode == hash && node.Key.Equals(key))
                {
                    // Unlink from chain
                    if (_buckets[bucketIndex] - 1 == indexToRemove)
                    {
                        _buckets[bucketIndex] = node.Previous + 1;
                    }
                    else
                    {
                        _nodes[itemAfterCurrent].Previous = node.Previous;
                    }
                    break;
                }
                itemAfterCurrent = indexToRemove;
                indexToRemove = node.Previous;
            }

            if (indexToRemove == -1)
            {
                removedValue = default;
                return false;
            }

            removedValue = _values[indexToRemove];
            var lastIndex = _header->Count - 1;

            // Swap-back: move last entry into the removed slot
            if (indexToRemove != lastIndex)
            {
                ref var nodeToMove = ref _nodes[lastIndex];
                var movingBucketIndex = TrecsDictionary.Reduce(
                    (uint)nodeToMove.HashCode,
                    bucketsLength,
                    _header->FastModMultiplier
                );

                // Fix the moved entry's chain references
                var linkedListIter = _buckets[movingBucketIndex] - 1;
                if (linkedListIter == lastIndex)
                {
                    _buckets[movingBucketIndex] = indexToRemove + 1;
                }

                while (
                    _nodes[linkedListIter].Previous != -1
                    && _nodes[linkedListIter].Previous != lastIndex
                )
                {
                    linkedListIter = _nodes[linkedListIter].Previous;
                }

                if (_nodes[linkedListIter].Previous != -1)
                {
                    _nodes[linkedListIter].Previous = indexToRemove;
                }

                _nodes[indexToRemove] = nodeToMove;
                _values[indexToRemove] = _values[lastIndex];
            }

            _header->Count--;
            BumpVersionAndResync();
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            CheckWriteAccess();
            if (_header->Count == 0)
                return;

            _header->Count = 0;
            _header->Collisions = 0;
            if (_buckets != null && _header->BucketCount > 0)
            {
                UnsafeUtility.MemClear(_buckets, _header->BucketCount * sizeof(int));
            }
            BumpVersionAndResync();
        }

        public void EnsureCapacity(
            int minCapacity,
            [CallerFilePath] string callerFile = null,
            [CallerLineNumber] int callerLine = 0
        )
        {
            CheckWriteAccess();
            TrecsDebugAssert.That(minCapacity >= 0, "minCapacity must be non-negative");
            if (minCapacity <= _header->EntryCapacity)
                return;

            var newEntryCapacity = TrecsDictionary.ComputeNewEntryCapacity(
                _header->EntryCapacity,
                minCapacity,
                _header->NodeSize,
                _header->ValueSize
            );
            var newBucketCount = HashHelpers.GetPrime(newEntryCapacity);
            Grow(newEntryCapacity, newBucketCount, callerFile, callerLine);
            BumpVersionAndResync();
        }

        public KeyEnumerable Keys
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                CheckReadAccess();
                return new KeyEnumerable(_nodes, _header->Count);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator()
        {
            CheckReadAccess();
            return new Enumerator(_nodes, _values, _header->Count);
        }

        // ─── Internal ────────────────────────────────────────────────

        bool AddValue(TKey key, out int indexSet)
        {
            // Ensure we have a data slot and at least one bucket
            if (_header->BucketCount == 0)
            {
                var initCap = 4;
                var initBuckets = HashHelpers.GetPrime(initCap);
                Grow(initCap, initBuckets);
            }

            int hash = key.GetHashCode();
            int bucketIndex = TrecsDictionary.Reduce(
                (uint)hash,
                (uint)_header->BucketCount,
                _header->FastModMultiplier
            );

            var valueIndex = _buckets[bucketIndex] - 1;

            if (valueIndex == -1)
            {
                // No chain at this bucket — fast path
                AppendEntry(key, hash, -1);
            }
            else
            {
                // Walk chain to check for duplicate
                int currentValueIndex = valueIndex;
                do
                {
                    ref var node = ref _nodes[currentValueIndex];
                    if (node.HashCode == hash && node.Key.Equals(key))
                    {
                        indexSet = currentValueIndex;
                        return false;
                    }
                    currentValueIndex = node.Previous;
                } while (currentValueIndex != -1);

                _header->Collisions++;
                AppendEntry(key, hash, valueIndex);
            }

            indexSet = _header->Count - 1;
            _buckets[bucketIndex] = indexSet + 1;

            if (_header->Collisions > _header->BucketCount)
            {
                var newBucketCount =
                    _header->BucketCount < 100
                        ? _header->Collisions << 1
                        : HashHelpers.Expand(_header->Collisions);
                // Keep current entry capacity, just rehash
                Grow(_header->EntryCapacity, newBucketCount);
            }

            return true;
        }

        void AppendEntry(TKey key, int hash, int previous)
        {
            var count = _header->Count;
            if (count == _header->EntryCapacity)
            {
                var newCap = TrecsDictionary.ComputeNewEntryCapacity(
                    _header->EntryCapacity,
                    count + 1,
                    _header->NodeSize,
                    _header->ValueSize
                );
                var newBuckets = HashHelpers.GetPrime(newCap);
                Grow(newCap, newBuckets);
                // After grow, the bucket structure is rebuilt — previous pointer is
                // no longer valid. The caller (AddValue) will re-set the bucket head
                // after this returns, but we need to fixup previous for the newly
                // appended entry. Since Grow rebuilds all chains, we set previous to
                // whatever the bucket currently points at.
                int bucketIndex = TrecsDictionary.Reduce(
                    (uint)hash,
                    (uint)_header->BucketCount,
                    _header->FastModMultiplier
                );
                previous = _buckets[bucketIndex] - 1;
            }

            _nodes[count] =
                previous == -1
                    ? new IterableDictionaryNode<TKey>(key, hash)
                    : new IterableDictionaryNode<TKey>(key, hash, previous);
            _values[count] = default;
            _header->Count = count + 1;
        }

        void Grow(
            int newEntryCapacity,
            int newBucketCount,
            string callerFile = null,
            int callerLine = 0
        )
        {
            TrecsDictionary<TKey, TValue>.GrowDataSlot<TKey, TValue>(
                _store,
                _header,
                newEntryCapacity,
                newBucketCount,
                callerFile,
                callerLine
            );
            ResyncDataPointers();
        }

        void ResyncDataPointers()
        {
            if (_header->DataHandle.IsNull)
            {
                _nodes = null;
                _values = null;
                _buckets = null;
                return;
            }
            var dataEntry = _store.ResolveEntry(_header->DataHandle);
            var dataBase = (byte*)dataEntry.Address.ToPointer();
            _nodes = (IterableDictionaryNode<TKey>*)dataBase;
            var valuesOffset = TrecsDictionary.ValuesOffset(
                _header->EntryCapacity,
                _header->NodeSize,
                _header->ValueAlign
            );
            _values = (TValue*)(dataBase + valuesOffset);
            var bucketsOffset = TrecsDictionary.BucketsOffset(
                _header->EntryCapacity,
                _header->NodeSize,
                _header->ValueAlign,
                _header->ValueSize
            );
            _buckets = (int*)(dataBase + bucketsOffset);
            // Re-capture the version that GrowDataSlot bumped
            _capturedVersion = _header->Version;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool TryGetIndexInternal(TKey key, out int findIndex)
        {
            if (_header->BucketCount == 0)
            {
                findIndex = 0;
                return false;
            }

            int hash = key.GetHashCode();
            int bucketIndex = TrecsDictionary.Reduce(
                (uint)hash,
                (uint)_header->BucketCount,
                _header->FastModMultiplier
            );
            int valueIndex = _buckets[bucketIndex] - 1;

            while (valueIndex != -1)
            {
                ref var node = ref _nodes[valueIndex];
                if (node.HashCode == hash && node.Key.Equals(key))
                {
                    findIndex = valueIndex;
                    return true;
                }
                valueIndex = node.Previous;
            }

            findIndex = 0;
            return false;
        }

        // ─── Enumerators ─────────────────────────────────────────────

        public readonly ref struct KeyEnumerable
        {
            readonly IterableDictionaryNode<TKey>* _nodes;
            readonly int _count;

            internal KeyEnumerable(IterableDictionaryNode<TKey>* nodes, int count)
            {
                _nodes = nodes;
                _count = count;
            }

            public KeyEnumerator GetEnumerator() => new KeyEnumerator(_nodes, _count);
        }

        public ref struct KeyEnumerator
        {
            readonly IterableDictionaryNode<TKey>* _nodes;
            readonly int _count;
            int _index;

            internal KeyEnumerator(IterableDictionaryNode<TKey>* nodes, int count)
            {
                _nodes = nodes;
                _count = count;
                _index = -1;
            }

            public TKey Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _nodes[_index].Key;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                _index++;
                return _index < _count;
            }
        }

        public ref struct Enumerator
        {
            readonly IterableDictionaryNode<TKey>* _nodes;
            readonly TValue* _values;
            readonly int _count;
            int _index;

            internal Enumerator(IterableDictionaryNode<TKey>* nodes, TValue* values, int count)
            {
                _nodes = nodes;
                _values = values;
                _count = count;
                _index = -1;
            }

            public KeyValuePair Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => new KeyValuePair(_nodes[_index].Key, _values, _index);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                _index++;
                return _index < _count;
            }
        }

        public readonly ref struct KeyValuePair
        {
            readonly TValue* _valuesPtr;
            readonly int _index;

            public readonly TKey Key;

            internal KeyValuePair(TKey key, TValue* valuesPtr, int index)
            {
                Key = key;
                _valuesPtr = valuesPtr;
                _index = index;
            }

            public void Deconstruct(out TKey key, out TValue value)
            {
                key = Key;
                value = Value;
            }

            public ref TValue Value
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _valuesPtr[_index];
            }
        }
    }
}
