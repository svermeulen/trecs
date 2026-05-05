using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Read-only view of a <see cref="SetGroupEntry"/>.
    /// Exposes membership checks and count but no mutation.
    /// </summary>
    public readonly struct SetGroupEntryRead
    {
        readonly NativeDenseDictionary<int, int> _entityIdToDenseIndex;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SetGroupEntryRead(SetGroupEntry entry)
        {
            _entityIdToDenseIndex = entry._entityIdToDenseIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(int index) => _entityIdToDenseIndex.ContainsKey(index);

        public EntitySetIndices Indices
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var values = _entityIdToDenseIndex.UnsafeValues;
                return new EntitySetIndices(values, _entityIdToDenseIndex.Count);
            }
        }

        public int this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entityIdToDenseIndex[index];
        }

        public int Count => _entityIdToDenseIndex.Count;
        public bool IsValid => _entityIdToDenseIndex.IsCreated;
    }
}
