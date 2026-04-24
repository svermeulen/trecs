using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

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
        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<SetGroupEntry> _entriesPerGroup;

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
            if (group.IsNull)
            {
                groupEntry = default;
                return false;
            }
            var entry = _entriesPerGroup[group.Index];
            if (entry.IsValid)
            {
                groupEntry = new SetGroupEntryRead(entry);
                return true;
            }
            groupEntry = default;
            return false;
        }
    }
}
