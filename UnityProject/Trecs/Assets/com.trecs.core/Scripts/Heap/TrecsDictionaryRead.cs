using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    public readonly unsafe ref struct TrecsDictionaryRead<TKey, TValue>
        where TKey : unmanaged, IEquatable<TKey>
        where TValue : unmanaged
    {
        readonly TrecsDictionaryHeader* _header;
        readonly IterableDictionaryNode<TKey>* _nodes;
        readonly TValue* _values;
        readonly int* _buckets;
        readonly ushort _capturedVersion;
        readonly NativeHeapEntry* _headerSlot;
        readonly byte _capturedGeneration;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        readonly AtomicSafetyHandle m_Safety;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsDictionaryRead(
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
            _capturedVersion = header->Version;
            _headerSlot = headerSlot;
            _capturedGeneration = capturedGeneration;
            m_Safety = safety;
        }
#else
        [EditorBrowsable(EditorBrowsableState.Never)]
        public TrecsDictionaryRead(
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
                "TrecsDictionaryRead is stale: the underlying TrecsDictionary allocation "
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
                "TrecsDictionaryRead is stale: the dictionary was mutated since this "
                    + "wrapper was opened (captured version {0}, current {1}). Re-open "
                    + "the wrapper after any mutation.",
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
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(TKey key, out int findIndex)
        {
            CheckReadAccess();
            return TryGetIndexInternal(key, out findIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref readonly TValue GetValueAtIndex(int index)
        {
            CheckReadAccess();
            TrecsDebugAssert.That(
                (uint)index < (uint)_header->Count,
                "TrecsDictionaryRead.GetValueAtIndex index {0} out of range (Count={1})",
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
                "TrecsDictionaryRead.GetKeyAtIndex index {0} out of range (Count={1})",
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

            public ref readonly TValue Value
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _valuesPtr[_index];
            }
        }
    }
}
