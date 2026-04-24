using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Read-only view over a set's group-to-entities mapping.
    /// Uses the same <see cref="SetGroupEntry"/> structure internally.
    /// Used internally by query iterators for unified set iteration.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal readonly struct SetGroupLookup
    {
        readonly NativeDenseDictionary<GroupIndex, SetGroupEntry> _entriesPerGroup;

        internal SetGroupLookup(in EntitySet set)
        {
            _entriesPerGroup = set._entriesPerGroup;
        }

        public bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entriesPerGroup.IsCreated;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetGroupEntry(GroupIndex group, out SetGroupEntryRead groupEntry)
        {
            if (_entriesPerGroup.TryGetValue(group, out var entry))
            {
                groupEntry = new SetGroupEntryRead(entry);
                return true;
            }
            groupEntry = default;
            return false;
        }
    }
}
