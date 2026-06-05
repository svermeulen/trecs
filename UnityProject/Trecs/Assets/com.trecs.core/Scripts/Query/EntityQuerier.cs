using System;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    public sealed class EntityQuerier
    {
        readonly TrecsLog _log;

        readonly ComponentStore _componentStore;
        readonly SetStore _setStore;

        internal readonly EntityHandleMap _entityLocator;

        // During OnRemoved callbacks, removed entities sit in the tail of each
        // group's component arrays at indices [newCount, originalCount). Normal
        // bounds checks reject those indices because ComponentArray.Count has
        // already been shrunk to newCount. This array stores the pre-removal
        // count (originalCount) per group so that dynamic component lookups
        // (EntityIndex.Component<T>) succeed for removed entities during the
        // callback window. Liveness checks (Exists, EntityIndexExists) are
        // intentionally unchanged. Zero means "no extension active".
        readonly int[] _removalExtendedCounts;

        internal EntityQuerier(
            TrecsLog log,
            ComponentStore componentStore,
            SetStore setStore,
            int groupCount
        )
        {
            _entityLocator = new EntityHandleMap(groupCount);
            _removalExtendedCounts = new int[groupCount];

            _log = log;
            _componentStore = componentStore;
            _setStore = setStore;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetRemovalExtendedCount(GroupIndex group, int originalCount)
        {
            _removalExtendedCounts[group.Index] = originalCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ClearRemovalExtendedCounts()
        {
            Array.Clear(_removalExtendedCounts, 0, _removalExtendedCounts.Length);
        }

        // ── Entity ID resolution ────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetEntityIndex(EntityHandle entityHandle, out EntityIndex entityIndex)
        {
            return _entityLocator.TryGetEntityIndex(entityHandle, out entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndex GetEntityIndex(EntityHandle entityHandle)
        {
            return _entityLocator.GetEntityIndex(entityHandle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityHandle GetEntityHandle(EntityIndex entityIndex)
        {
            return _entityLocator.GetEntityHandle(entityIndex);
        }

        // ── Component queries ───────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists<T>(EntityIndex entityGID)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(entityGID.GroupIndex, out var casted))
                return false;

            return casted != null && entityGID.Index < casted.Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists<T>(int id, GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(group, out var casted))
                return false;

            return casted != null && id < casted.Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Count<T>(GroupIndex groupStruct)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(groupStruct, out var typeSafeDictionary))
                return 0;

            return typeSafeDictionary.Count;
        }

        /// <summary>
        /// Returns true if the given <see cref="EntityIndex"/> points to valid data in the
        /// component database (i.e. the entity has been submitted).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EntityIndexExists(EntityIndex entityIndex)
        {
            var componentMap = _componentStore.GetDBGroup(entityIndex.GroupIndex);

            foreach (var (_, componentArray) in componentMap)
            {
                return entityIndex.Index < componentArray.Count;
            }

            return false;
        }

        public int CountEntitiesInGroup(GroupIndex group)
        {
            TrecsDebugAssert.That(
                !group.IsNull && group.Index < _componentStore.GroupCount,
                "Attempted to get count for unrecognized group {0}",
                group
            );
            var entitiesInGroupPerType = _componentStore.GetDBGroup(group);

            // Zero-component templates (e.g. tag/filter-only) have no
            // component arrays, so we can't sample from one. They also can't
            // hold per-entity data, so the live count is always 0.
            if (entitiesInGroupPerType.Count == 0)
            {
                return 0;
            }

            // All component arrays in a group are parallel arrays with the same
            // count, so sampling the first is enough. Direct indexed access
            // (no enumerator, no nullable) — this runs per group per query
            // iteration.
            int count = entitiesInGroupPerType.UnsafeValues[0].Count;

#if DEBUG
            foreach (var (_, value) in entitiesInGroupPerType)
            {
                TrecsDebugAssert.IsEqual(count, value.Count);
            }
#endif

            return count;
        }

        // ── Native query methods ────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeBuffer<T> QueryEntitiesAndIndex<T>(EntityIndex entityGID, out int index)
            where T : unmanaged, IEntityComponent
        {
            if (
                QueryEntitiesAndIndexInternal(
                    entityGID,
                    out index,
                    out NativeBuffer<T> array,
                    out var failReason
                )
            )
                return array;

            throw new TrecsException(FormatQueryFailMessage<T>(entityGID, failReason));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeBuffer<T> QueryEntitiesAndIndex<T>(int id, GroupIndex group, out int index)
            where T : unmanaged, IEntityComponent
        {
            EntityIndex entityGID = new EntityIndex(id, group);
            if (
                QueryEntitiesAndIndexInternal(
                    entityGID,
                    out index,
                    out NativeBuffer<T> array,
                    out var failReason
                )
            )
                return array;

            throw new TrecsException(FormatQueryFailMessage<T>(entityGID, failReason));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryQueryEntitiesAndIndex<T>(
            EntityIndex entityGID,
            out int index,
            out NativeBuffer<T> array
        )
            where T : unmanaged, IEntityComponent
        {
            return QueryEntitiesAndIndexInternal(entityGID, out index, out array, out _);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryQueryEntitiesAndIndex<T>(
            int id,
            GroupIndex group,
            out int index,
            out NativeBuffer<T> array
        )
            where T : unmanaged, IEntityComponent
        {
            return QueryEntitiesAndIndexInternal(
                new EntityIndex(id, group),
                out index,
                out array,
                out _
            );
        }

        static string FormatQueryFailMessage<T>(EntityIndex entityGID, QueryFailReason failReason)
        {
            var baseMsg =
                $"Entity with index '{entityGID.Index}', group '{entityGID.GroupIndex}' and component '{typeof(T)}'";

            if (failReason == QueryFailReason.IndexOutOfRange)
            {
                return $"{baseMsg}: index is out of range. "
                    + "If you are accessing this data outside an OnRemoved callback, note that "
                    + "removed entities cannot be accessed after submission completes. Inside an "
                    + "OnRemoved callback, EntityIndex.Component<T>() and [ForEachEntity] parameters "
                    + "both support accessing removed entity data.";
            }

            return $"{baseMsg} not found!";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetEntity<T>(int entityHandle, GroupIndex @group, out T value)
            where T : unmanaged, IEntityComponent
        {
            if (TryQueryEntitiesAndIndex<T>(entityHandle, group, out var index, out var array))
            {
                value = array[index];
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetEntity<T>(EntityIndex entityIndex, out T value)
            where T : unmanaged, IEntityComponent
        {
            return TryGetEntity<T>(entityIndex.Index, entityIndex.GroupIndex, out value);
        }

        // ── NativeComponentLookup builders ────────────────────────

        /// <summary>
        /// Allocate and populate the raw <see cref="NativeComponentLookupEntry"/> array that
        /// backs a <see cref="Trecs.NativeComponentLookupRead{T}"/> /
        /// <see cref="Trecs.NativeComponentLookupWrite{T}"/>. One entry per group that
        /// (a) matches the lookup's set and (b) actually contains entities with
        /// component <typeparamref name="T"/>. Allocated via <c>MallocTracked</c> so the
        /// allocation participates in Unity's leak detector; freed by the lookup's
        /// <c>Dispose</c> via <c>FreeTracked</c>.
        /// </summary>
        internal unsafe void BuildNativeComponentLookupEntries<T>(
            ReadOnlyList<GroupIndex> groups,
            Allocator allocator,
            out NativeComponentLookupEntry* entries,
            out int count
        )
            where T : unmanaged, IEntityComponent
        {
            // First pass: count groups that actually contribute an entry. Saves us
            // from over-allocating when most candidate groups are empty.
            int matchingCount = 0;
            foreach (var group in groups)
            {
                if (
                    SafeQueryEntityDictionary<T>(group, out var typeSafeDictionary)
                    && typeSafeDictionary.Count > 0
                )
                {
                    matchingCount++;
                }
            }

            if (matchingCount == 0)
            {
                entries = null;
                count = 0;
                return;
            }

            var byteSize = (long)matchingCount * UnsafeUtility.SizeOf<NativeComponentLookupEntry>();
            entries = (NativeComponentLookupEntry*)
                UnsafeUtility.MallocTracked(
                    byteSize,
                    UnsafeUtility.AlignOf<NativeComponentLookupEntry>(),
                    allocator,
                    callstacksToSkip: 1
                );

            int writeIdx = 0;
            foreach (var group in groups)
            {
                if (
                    SafeQueryEntityDictionary<T>(group, out var typeSafeDictionary)
                    && typeSafeDictionary.Count > 0
                )
                {
                    // Grab the raw pointer directly from the underlying ComponentArray.
                    // The walker never traverses through this pointer (it's behind
                    // [NativeDisableUnsafePtrRestriction] on the lookup struct), so we
                    // bypass the ComponentArray's NativeList atomic safety handle entirely.
                    var componentArray = (IComponentArray<T>)typeSafeDictionary;
                    var buffer = componentArray.GetValues(out var entryCount);
                    entries[writeIdx] = new NativeComponentLookupEntry
                    {
                        GroupIndex = group,
                        DataPtr = buffer.GetRawPointer(out _),
                        Count = entryCount,
                    };
                    writeIdx++;
                }
            }

            count = writeIdx;
        }

        // ── Internal helpers ────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool SafeQueryEntityDictionary<T>(
            IterableDictionary<TypeId, IComponentArray> entitiesInGroupPerType,
            out IComponentArray typeSafeDictionary
        )
            where T : unmanaged, IEntityComponent
        {
            if (!entitiesInGroupPerType.TryGetValue(TypeId<T>.Value, out var safeDictionary))
            {
                typeSafeDictionary = default;
                return false;
            }

            typeSafeDictionary = safeDictionary;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool SafeQueryEntityDictionary<T>(
            GroupIndex group,
            out IComponentArray typeSafeDictionary
        )
            where T : unmanaged, IEntityComponent
        {
            var entitiesInGroupPerType = _componentStore.GetDBGroup(group);

            if (!entitiesInGroupPerType.TryGetValue(TypeId<T>.Value, out var safeDictionary))
            {
                typeSafeDictionary = default;
                return false;
            }

            typeSafeDictionary = safeDictionary;
            return true;
        }

        internal NativeBuffer<T> QuerySingleBuffer<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            var entitiesInGroupPerType = _componentStore.GetDBGroup(group);

            if (!SafeQueryEntityDictionary<T>(entitiesInGroupPerType, out var typeSafeDictionary))
                return default;

            return ((IComponentArray<T>)typeSafeDictionary).GetValues(out _);
        }

        internal (NativeBuffer<T> buffer, int count) QuerySingleBufferWithCount<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            var entitiesInGroupPerType = _componentStore.GetDBGroup(group);

            if (!SafeQueryEntityDictionary<T>(entitiesInGroupPerType, out var typeSafeDictionary))
                return (default, 0);

            var buffer = ((IComponentArray<T>)typeSafeDictionary).GetValues(out var count);
            return (buffer, (int)count);
        }

        internal enum QueryFailReason
        {
            None,
            ComponentNotFound,
            IndexOutOfRange,
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool QueryEntitiesAndIndexInternal<T>(
            EntityIndex entityGID,
            out int index,
            out NativeBuffer<T> buffer,
            out QueryFailReason failReason
        )
            where T : unmanaged, IEntityComponent
        {
            index = entityGID.Index;
            buffer = default;
            failReason = QueryFailReason.None;

            if (!SafeQueryEntityDictionary<T>(entityGID.GroupIndex, out var safeDictionary))
            {
                failReason = QueryFailReason.ComponentNotFound;
                return false;
            }

            if (index >= safeDictionary.Count)
            {
                var extCount = _removalExtendedCounts[entityGID.GroupIndex.Index];
                if (extCount == 0 || index >= extCount)
                {
                    failReason = QueryFailReason.IndexOutOfRange;
                    return false;
                }
            }

            buffer = (safeDictionary as IComponentArray<T>).GetValues(out _);

            return true;
        }
    }
}
