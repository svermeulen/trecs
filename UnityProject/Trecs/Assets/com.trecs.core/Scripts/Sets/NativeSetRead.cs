using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Read-only, Burst-compatible accessor for checking entity membership in a set.
    /// Usable as a <c>[FromWorld]</c> field in job structs.
    ///
    /// Tracked as a <b>read dependency</b> by the job scheduler.
    ///
    /// For deferred mutations use <see cref="NativeWorldAccessor.SetAdd{TSet}"/>
    /// and <see cref="NativeWorldAccessor.SetRemove{TSet}"/> directly.
    /// </summary>
    public struct NativeSetRead<TSet>
        where TSet : struct, IEntitySet
    {
        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<SetGroupEntry> _entriesPerGroup;

        internal NativeSetRead(in EntitySetStorage set)
        {
            _entriesPerGroup = set._entriesPerGroup;
        }

        public bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entriesPerGroup.IsCreated;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Exists(EntityIndex entityIndex)
        {
            var group = entityIndex.GroupIndex;
            if (group.IsNull)
                return false;
            var entry = _entriesPerGroup[group.Index];
            return entry.IsValid && entry.Exists(entityIndex.Index);
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
