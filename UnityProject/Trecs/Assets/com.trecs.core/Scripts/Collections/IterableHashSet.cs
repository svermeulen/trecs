using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    /// Managed hash set with deterministic iteration order. Backed by an
    /// <see cref="IterableDictionary{TKey,TValue}"/> internally.
    /// Use when you need deterministic iteration order (required for simulation
    /// correctness in Trecs). Faster Contains than HashSet under IL2CPP; faster
    /// iteration than NativeHashSet. Slower Add/Remove than HashSet due to
    /// dual-array bookkeeping.
    /// Does not implement IEnumerable by design: foreach over an interface-typed
    /// variable boxes the struct enumerator, causing a GC allocation per iteration.
    /// Use the concrete type or ReadOnlyIterableHashSet for zero-alloc foreach.
    /// </summary>
    public sealed class IterableHashSet<T>
        where T : struct, IEquatable<T>
    {
        private static readonly HashSetEmptyValue EMPTY_VALUE = new();
        private readonly IterableDictionary<T, HashSetEmptyValue> _dictionary;

        public IterableHashSet()
            : this(1) { }

        public IterableHashSet(int size)
        {
            _dictionary = new IterableDictionary<T, HashSetEmptyValue>(size);
        }

        public bool IsEmpty => _dictionary.Count == 0;

        public IterableDictionaryNode<T>[] UnsafeValues
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dictionary.UnsafeKeys;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dictionary.Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Add(T item)
        {
            if (_dictionary.ContainsKey(item))
                return false;

            _dictionary.Add(item, EMPTY_VALUE);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _dictionary.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Recycle()
        {
            _dictionary.Recycle();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T item)
        {
            return _dictionary.ContainsKey(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int size)
        {
            _dictionary.EnsureCapacity(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncreaseCapacityBy(int size)
        {
            _dictionary.IncreaseCapacityBy(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(T item)
        {
            return _dictionary.TryRemove(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveMustExist(T item)
        {
            var wasRemoved = _dictionary.TryRemove(item);
            TrecsDebugAssert.That(wasRemoved);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Trim()
        {
            _dictionary.Trim();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(T item, out int findIndex)
        {
            return _dictionary.TryGetIndex(item, out findIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(T item)
        {
            return _dictionary.GetIndex(item);
        }

        public static IterableHashSet<T> Construct()
        {
            return new IterableHashSet<T>(0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyElementsTo(List<T> elements)
        {
            foreach (var key in _dictionary.Keys)
            {
                elements.Add(key);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyElementsTo(T[] array, int index = 0)
        {
            int i = 0;
            foreach (var key in _dictionary.Keys)
            {
                array[index + i++] = key;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IntersectWith(IterableHashSet<T> other)
        {
            _dictionary.Intersect(other._dictionary);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExceptWith(IterableHashSet<T> other)
        {
            _dictionary.Exclude(other._dictionary);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnionWith(IterableHashSet<T> other)
        {
            foreach (var key in other._dictionary.Keys)
            {
                if (!Contains(key))
                {
                    Add(key);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator()
        {
            return new Enumerator(_dictionary);
        }

        public struct Enumerator
        {
            private readonly IterableDictionary<T, HashSetEmptyValue> _dictionary;
            private int _index;
            private readonly int _count;

            internal Enumerator(IterableDictionary<T, HashSetEmptyValue> dictionary)
            {
                _dictionary = dictionary;
                _index = -1;
                _count = dictionary.Count;
            }

            public bool MoveNext()
            {
                if (_index < _count - 1)
                {
                    ++_index;
                    return true;
                }
                return false;
            }

            public readonly T Current => _dictionary.UnsafeKeys[_index].Key;
        }

        // Empty value struct used as placeholder in the dictionary
        internal readonly struct HashSetEmptyValue { }
    }
}
