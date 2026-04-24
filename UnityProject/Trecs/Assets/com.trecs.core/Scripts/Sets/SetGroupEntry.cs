using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;

namespace Trecs
{
    /// <summary>
    /// Tracks a set of entity indices for a single group within an entity set.
    /// Used by <see cref="EntitySet"/>.
    /// </summary>
    public struct SetGroupEntry
    {
        internal NativeDenseDictionary<int, int> _entityIdToDenseIndex;
        readonly GroupIndex _group;

        internal SetGroupEntry(GroupIndex group)
            : this()
        {
            _entityIdToDenseIndex = new NativeDenseDictionary<int, int>(1, Allocator.Persistent);
            _group = group;
        }

        public readonly GroupIndex GroupIndex => _group;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Add(int index)
        {
            //cannot write in parallel
            return _entityIdToDenseIndex.TryAdd(index, index, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly bool Exists(int index) => _entityIdToDenseIndex.ContainsKey(index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(int index) => _entityIdToDenseIndex.Remove(index);

        public readonly EntitySetIndices Indices
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var values = _entityIdToDenseIndex.GetValuesRead(out var count);
                return new EntitySetIndices(values, count);
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
