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
    [NativeContainerIsReadOnly]
    public readonly unsafe struct NativeTrecsDictionaryRead<TKey, TValue>
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
            NativeTrecsDictionaryRead<TKey, TValue>
        >();
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeTrecsDictionaryRead(
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
            CollectionHelper.SetStaticSafetyId<NativeTrecsDictionaryRead<TKey, TValue>>(
                ref m_Safety,
                ref s_staticSafetyId.Data
            );
            CheckSlotAlive();
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public NativeTrecsDictionaryRead(
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
                "NativeTrecsDictionaryRead is stale: the underlying allocation has been "
                    + "freed (captured slot gen {0}, current {1}, InUse {2}).",
                _capturedGeneration,
                _headerSlot->Generation,
                _headerSlot->InUse
            );
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
        public ref readonly TValue GetValueAtIndex(int index)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "NativeTrecsDictionaryRead.GetValueAtIndex index {0} out of range (Count={1})",
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
                "NativeTrecsDictionaryRead.GetKeyAtIndex index {0} out of range (Count={1})",
                index,
                _header->Count
            );
            return _nodes[index].Key;
        }

        public KeyEnumerable Keys
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return new KeyEnumerable(_nodes, _header->Count);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
            return new Enumerator(_nodes, _values, _header->Count);
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

        public readonly struct KeyEnumerable
        {
            [NativeDisableUnsafePtrRestriction]
            readonly IterableDictionaryNode<TKey>* _nodes;
            readonly int _count;

            internal KeyEnumerable(IterableDictionaryNode<TKey>* nodes, int count)
            {
                _nodes = nodes;
                _count = count;
            }

            public KeyEnumerator GetEnumerator() => new KeyEnumerator(_nodes, _count);
        }

        public struct KeyEnumerator
        {
            [NativeDisableUnsafePtrRestriction]
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

            public ref readonly TValue Value
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _valuesPtr[_index];
            }
        }
    }
}
