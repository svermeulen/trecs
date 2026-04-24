using System.Runtime.CompilerServices;
using Trecs.Internal;

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
        readonly NativeDenseDictionary<GroupIndex, SetGroupEntry> _entriesPerGroup;

        internal NativeSetRead(in EntitySet set)
        {
            _entriesPerGroup = set._entriesPerGroup;
        }

        public bool IsCreated
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entriesPerGroup.IsCreated;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(EntityIndex entityIndex)
        {
            if (_entriesPerGroup.TryGetValue(entityIndex.GroupIndex, out var groupEntry))
                return groupEntry.Exists(entityIndex.Index);
            return false;
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
