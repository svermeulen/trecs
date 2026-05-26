using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;

namespace Trecs
{
    /// <summary>
    /// Tracks a set of entity indices for a single group within an entity set.
    /// Used by <see cref="EntitySetStorage"/>.
    /// </summary>
    public struct SetGroupEntry
    {
        internal NativeIterableDictionary<int, int> _entityIdToDenseIndex;
        readonly GroupIndex _group;

        internal SetGroupEntry(GroupIndex group)
            : this()
        {
            _entityIdToDenseIndex = new NativeIterableDictionary<int, int>(1, Allocator.Persistent);
            _group = group;
        }

        public readonly GroupIndex GroupIndex => _group;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Add(int index)
        {
            // Not parallel-safe
            return _entityIdToDenseIndex.TryAdd(index, index, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Contains(int index) => _entityIdToDenseIndex.ContainsKey(index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(int index) => _entityIdToDenseIndex.Remove(index);

        public readonly EntitySetIndices Indices
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var values = _entityIdToDenseIndex.UnsafeValues;
                return new EntitySetIndices(
                    values,
                    _entityIdToDenseIndex.Count,
                    _entityIdToDenseIndex
                );
            }
        }

        public int this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entityIdToDenseIndex[index];
        }

        public readonly int Count => _entityIdToDenseIndex.Count;
        public readonly bool IsValid => _entityIdToDenseIndex.IsCreated;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Clear()
        {
            _entityIdToDenseIndex.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Dispose()
        {
            _entityIdToDenseIndex.Dispose();
        }
    }
}
