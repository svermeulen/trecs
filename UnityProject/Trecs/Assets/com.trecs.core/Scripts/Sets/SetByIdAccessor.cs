using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Lightweight untyped set gateway returned by <see cref="WorldAccessor.Set(SetId)"/>.
    /// Provides a synchronous read view for tooling and editor code that operates on
    /// runtime <see cref="SetId"/> values. For typed access use <see cref="WorldAccessor.Set{T}"/>.
    /// </summary>
    public readonly ref struct SetByIdAccessor
    {
        readonly WorldAccessor _world;
        readonly SetId _setId;

        internal SetByIdAccessor(WorldAccessor world, SetId setId)
        {
            _world = world;
            _setId = setId;
        }

        /// <summary>
        /// Returns a synchronous read-only view after syncing outstanding writer jobs.
        /// </summary>
        public SetReadById Read
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                _world.SyncSetForRead(_setId);
                return new SetReadById(_world.GetSetCollection(_setId));
            }
        }
    }

    /// <summary>
    /// Untyped read-only set view returned by <see cref="SetByIdAccessor.Read"/>.
    /// Outstanding writer jobs have been synced before this view is constructed.
    /// </summary>
    public readonly ref struct SetReadById
    {
        [NativeDisableContainerSafetyRestriction]
        readonly NativeList<SetGroupEntry> _entriesPerGroup;

        readonly NativeList<GroupIndex> _registeredGroups;

        internal SetReadById(in EntitySetStorage set)
        {
            _entriesPerGroup = set._entriesPerGroup;
            _registeredGroups = set._registeredGroups;
        }

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int count = 0;
                for (int i = 0; i < _registeredGroups.Length; i++)
                {
                    count += _entriesPerGroup[_registeredGroups[i].Index].Count;
                }
                return count;
            }
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
    }
}
