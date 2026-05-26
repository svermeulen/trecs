using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    [NativeContainer]
    public unsafe struct NativeTrecsDictionaryWrite<TKey, TValue>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        [NativeDisableUnsafePtrRestriction]
        readonly TrecsDictionaryHeader* _header;

        [NativeDisableUnsafePtrRestriction]
        readonly IterableDictionaryNode<TKey>* _nodes;

        [NativeDisableUnsafePtrRestriction]
        readonly TValue* _values;

        [NativeDisableUnsafePtrRestriction]
        readonly int* _buckets;

        [NativeDisableUnsafePtrRestriction]
        readonly NativeHeapEntry* _headerSlot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;

        static readonly SharedStatic<int> s_staticSafetyId = SharedStatic<int>.GetOrCreate<
            NativeTrecsDictionaryWrite<TKey, TValue>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeTrecsDictionaryWrite(
            TrecsDictionaryHeader* header,
            IterableDictionaryNode<TKey>* nodes,
            TValue* values,
            int* buckets,
            NativeHeapEntry* headerSlot,
            byte capturedGeneration,
            AtomicSafetyHandle safety
        )
        {
            _header = header;
            _nodes = nodes;
            _values = values;
            _buckets = buckets;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
            CollectionHelper.SetStaticSafetyId<NativeTrecsDictionaryWrite<TKey, TValue>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
            CheckSlotAlive();
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeTrecsDictionaryWrite(
            TrecsDictionaryHeader* header,
            IterableDictionaryNode<TKey>* nodes,
            TValue* values,
            int* buckets,
            NativeHeapEntry* headerSlot,
            byte capturedGeneration
        )
        {
            _header = header;
            _nodes = nodes;
            _values = values;
            _buckets = buckets;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
            CheckSlotAlive();
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CheckSlotAlive()
        {
            TrecsAssert.That(
                _headerSlot->Generation == _capturedGeneration && _headerSlot->InUse == 1,
                "NativeTrecsDictionaryWrite is stale: the underlying allocation has been "
                    + "freed (captured slot gen {0}, current {1}, InUse {2}).",
                _capturedGeneration,
                _headerSlot->Generation,
                _headerSlot->InUse
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void BumpVersion()
        {
            unchecked
            {
                ++_header->Version;
            }
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return _header->Count;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(TKey key)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            return TryGetIndexInternal(key, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(TKey key, out TValue value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
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
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                TrecsAssert.That(
                    TryGetIndexInternal(key, out var index),
                    "Key not found in TrecsDictionary"
                );
                return _values[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
                AddValue(key, out var index);
                _values[index] = value;
                BumpVersion();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(TKey key, in TValue value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            var added = AddValue(key, out var index);
            TrecsDebugAssert.That(added, "Key already present in TrecsDictionary");
            _values[index] = value;
            BumpVersion();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAdd(TKey key, in TValue value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            var added = AddValue(key, out var index);
            if (added)
            {
                _values[index] = value;
                BumpVersion();
            }
            return added;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(TKey key, in TValue value)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            TrecsAssert.That(
                TryGetIndexInternal(key, out var index),
                "Key not found in TrecsDictionary"
            );
            _values[index] = value;
            BumpVersion();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetOrAdd(TKey key)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            if (AddValue(key, out var index))
                BumpVersion();
            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueByRef(TKey key)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            TrecsAssert.That(
                TryGetIndexInternal(key, out var index),
                "Key not found in TrecsDictionary"
            );
            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref TValue GetValueAtIndex(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "NativeTrecsDictionaryWrite.GetValueAtIndex index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            return ref _values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TKey GetKeyAtIndex(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "NativeTrecsDictionaryWrite.GetKeyAtIndex index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            return _nodes[index].Key;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(TKey key, out int findIndex)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            return TryGetIndexInternal(key, out findIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(TKey key)
        {
            return Remove(key, out _);
        }

        public bool Remove(TKey key, out TValue removedValue)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif

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

            if (indexToRemove != lastIndex)
            {
                ref var nodeToMove = ref _nodes[lastIndex];
                var movingBucketIndex = TrecsDictionary.Reduce(
                    (uint)nodeToMove.HashCode,
                    bucketsLength,
                    _header->FastModMultiplier
                );

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
            BumpVersion();
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
            if (_header->Count == 0)
                return;

            _header->Count = 0;
            _header->Collisions = 0;
            if (_buckets != null && _header->BucketCount > 0)
            {
                UnsafeUtility.MemClear(_buckets, _header->BucketCount * sizeof(int));
            }
            BumpVersion();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            return new Enumerator(_nodes, _values, _header->Count);
        }

        // ─── Internal ────────────────────────────────────────────────

        bool AddValue(TKey key, out int indexSet)
        {
            TrecsAssert.That(
                _header->BucketCount > 0 && _header->EntryCapacity > 0,
                "NativeTrecsDictionaryWrite: dictionary has no capacity. "
                    + "Call EnsureCapacity on the main thread before scheduling."
            );

            int hash = key.GetHashCode();
            int bucketIndex = TrecsDictionary.Reduce(
                (uint)hash,
                (uint)_header->BucketCount,
                _header->FastModMultiplier
            );

            var valueIndex = _buckets[bucketIndex] - 1;

            if (valueIndex == -1)
            {
                TrecsAssert.That(
                    _header->Count < _header->EntryCapacity,
                    "NativeTrecsDictionaryWrite: capacity exceeded ({0}/{1}). "
                        + "Pre-size via EnsureCapacity before scheduling.",
                    _header->Count,
                    _header->EntryCapacity
                );
                _nodes[_header->Count] = new IterableDictionaryNode<TKey>(key, hash);
                _values[_header->Count] = default;
            }
            else
            {
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
                TrecsAssert.That(
                    _header->Count < _header->EntryCapacity,
                    "NativeTrecsDictionaryWrite: capacity exceeded ({0}/{1}). "
                        + "Pre-size via EnsureCapacity before scheduling.",
                    _header->Count,
                    _header->EntryCapacity
                );
                _nodes[_header->Count] = new IterableDictionaryNode<TKey>(key, hash, valueIndex);
                _values[_header->Count] = default;
            }

            indexSet = _header->Count;
            _buckets[bucketIndex] = indexSet + 1;
            _header->Count++;

            return true;
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

        // ─── Enumerator ──────────────────────────────────────────────

        public struct Enumerator
        {
            [NativeDisableUnsafePtrRestriction]
            readonly IterableDictionaryNode<TKey>* _nodes;

            [NativeDisableUnsafePtrRestriction]
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

        public readonly struct KeyValuePair
        {
            [NativeDisableUnsafePtrRestriction]
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
