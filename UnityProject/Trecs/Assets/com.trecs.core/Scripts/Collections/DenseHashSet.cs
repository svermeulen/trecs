using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs.Collections
{
    /// <summary>
    ///   Reasons to use this over standard C# HashSet:
    ///
    ///   1 - Fully deterministic iteration order. Most of the time this is by insertion order, except when removals happen, since
    ///       then the last item is moved to fill in the gap
    ///   2 - Can iterate over the keys as an array, directly, without generating one or using an iterator
    ///   3 - Provides better performance than the standard HashSet for most operations
    ///
    ///   Supports any element type that implements IEquatable<T>, including strings, enums, and custom types.
    ///   NOTE: accessing via IEnumerable is less performant and may contain boxing
    /// </summary>
    public sealed class DenseHashSet<T> : IEnumerable<T>
    {
        private static readonly HashSetEmptyValue EMPTY_VALUE = new HashSetEmptyValue();
        private readonly DenseDictionary<T, HashSetEmptyValue> _dictionary;

        public DenseHashSet()
            : this(1) { }

        public DenseHashSet(int size)
        {
            _dictionary = new DenseDictionary<T, HashSetEmptyValue>(size);
        }

        public bool IsEmpty => _dictionary.Count == 0;

        /// <summary>
        /// Gets the number of elements in the set.
        /// </summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dictionary.Count;
        }

        /// <summary>
        /// Adds an element to the set.
        /// </summary>
        /// <param name="item">The element to add.</param>
        /// <returns>True if the element was added, false if it was already present.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Add(T item)
        {
            if (_dictionary.ContainsKey(item))
                return false;

            _dictionary.Add(item, EMPTY_VALUE);
            return true;
        }

        /// <summary>
        /// Clears all elements from the set.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            _dictionary.Clear();
        }

        /// <summary>
        /// Recycles the set, clearing all elements but maintaining capacity.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Recycle()
        {
            _dictionary.Recycle();
        }

        /// <summary>
        /// Determines whether the set contains a specific element.
        /// </summary>
        /// <param name="item">The element to locate.</param>
        /// <returns>True if the set contains the element, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T item)
        {
            return _dictionary.ContainsKey(item);
        }

        /// <summary>
        /// Ensures the set can hold at least the specified number of elements.
        /// </summary>
        /// <param name="size">The minimum capacity to ensure.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnsureCapacity(int size)
        {
            _dictionary.EnsureCapacity(size);
        }

        /// <summary>
        /// Increases the capacity of the set by the specified number of elements.
        /// </summary>
        /// <param name="size">The number of elements to increase capacity by.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IncreaseCapacityBy(int size)
        {
            _dictionary.IncreaseCapacityBy(size);
        }

        /// <summary>
        /// Removes an element from the set.
        /// </summary>
        /// <param name="item">The element to remove.</param>
        /// <returns>True if the element was removed, false if it was not found.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryRemove(T item)
        {
            return _dictionary.TryRemove(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveMustExist(T item)
        {
            var wasRemoved = _dictionary.TryRemove(item);
            TrecsAssert.That(wasRemoved);
        }

        /// <summary>
        /// Trims excess capacity to minimize memory usage.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Trim()
        {
            _dictionary.Trim();
        }

        /// <summary>
        /// Tries to find the index of an element in the set.
        /// </summary>
        /// <param name="item">The element to locate.</param>
        /// <param name="findIndex">When this method returns, contains the index of the element if found; otherwise, 0.</param>
        /// <returns>True if the element was found, false otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetIndex(T item, out int findIndex)
        {
            return _dictionary.TryGetIndex(item, out findIndex);
        }

        /// <summary>
        /// Gets the index of an element in the set.
        /// </summary>
        /// <param name="item">The element to locate.</param>
        /// <returns>The index of the element.</returns>
        /// <exception cref="Exception">Thrown if the element is not found in the set.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetIndex(T item)
        {
            return _dictionary.GetIndex(item);
        }

        /// <summary>
        /// Creates a new DenseHashSet instance.
        /// </summary>
        /// <returns>A new DenseHashSet instance.</returns>
        public static DenseHashSet<T> Construct()
        {
            return new DenseHashSet<T>(0);
        }

        /// <summary>
        /// Copies the elements of the set to a FastList.
        /// </summary>
        /// <param name="elements">The FastList to copy elements to.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyElementsTo(List<T> elements)
        {
            foreach (var key in _dictionary.Keys)
            {
                elements.Add(key);
            }
        }

        /// <summary>
        /// Copies the elements of the set to an array.
        /// </summary>
        /// <param name="array">The array to copy elements to.</param>
        /// <param name="index">The starting index in the destination array.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyElementsTo(T[] array, int index = 0)
        {
            int i = 0;
            foreach (var key in _dictionary.Keys)
            {
                array[index + i++] = key;
            }
        }

        /// <summary>
        /// Modifies the current set to contain only elements that are present in both the current set and the specified collection.
        /// </summary>
        /// <param name="other">The collection to compare with the current set.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void IntersectWith(DenseHashSet<T> other)
        {
            _dictionary.Intersect(other._dictionary);
        }

        /// <summary>
        /// Removes all elements that are present in the specified collection from the current set.
        /// </summary>
        /// <param name="other">The collection of items to remove from the set.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExceptWith(DenseHashSet<T> other)
        {
            _dictionary.Exclude(other._dictionary);
        }

        /// <summary>
        /// Modifies the current set to contain all elements that are present in the current set, the specified collection, or both.
        /// </summary>
        /// <param name="other">The collection to compare with the current set.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnionWith(DenseHashSet<T> other)
        {
            foreach (var key in other._dictionary.Keys)
            {
                if (!Contains(key))
                {
                    Add(key);
                }
            }
        }

        /// <summary>
        /// Returns an enumerator that iterates through the set.
        /// </summary>
        /// <returns>An enumerator for the set.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Enumerator GetEnumerator()
        {
            return new Enumerator(_dictionary);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the set.
        /// </summary>
        /// <returns>An enumerator for the set.</returns>
        IEnumerator<T> IEnumerable<T>.GetEnumerator()
        {
            return new IEnumerableEnumerator(_dictionary);
        }

        /// <summary>
        /// Returns an enumerator that iterates through the set.
        /// </summary>
        /// <returns>An enumerator for the set.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return new IEnumerableEnumerator(_dictionary);
        }

        /// <summary>
        /// Enumerator for the DenseHashSet.
        /// </summary>
        public struct Enumerator
        {
            private readonly DenseDictionary<T, HashSetEmptyValue> _dictionary;
            private int _index;
            private readonly int _count;

            internal Enumerator(DenseDictionary<T, HashSetEmptyValue> dictionary)
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

            public T Current => _dictionary.UnsafeKeys[_index].key;
        }

        /// <summary>
        /// IEnumerable enumerator for compatibility with standard collections.
        /// </summary>
        private class IEnumerableEnumerator : IEnumerator<T>
        {
            private DenseDictionary<T, HashSetEmptyValue> _dictionary;
            private int _index;
            private readonly int _count;

            public IEnumerableEnumerator(DenseDictionary<T, HashSetEmptyValue> dictionary)
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

            public void Reset()
            {
                _index = -1;
            }

            public T Current => _dictionary.UnsafeKeys[_index].key;

            object IEnumerator.Current => Current;

            public void Dispose()
            {
                // No resources to dispose
            }
        }

        // Empty value struct used as placeholder in the dictionary
        internal readonly struct HashSetEmptyValue { }
    }
}
