using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    public class EntityQuerier
    {
        static readonly TrecsLog _log = new(nameof(EntityQuerier));

        readonly ComponentStore _componentStore;
        readonly SetStore _setStore;

        internal EntityHandleMap _entityLocator;

        internal EntityQuerier(ComponentStore componentStore, SetStore setStore, int groupCount)
        {
            _entityLocator.InitEntityHandleMap(groupCount);

            _componentStore = componentStore;
            _setStore = setStore;
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
        internal EntityHandleMap GetEntityHandleMap()
        {
            return _entityLocator;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityHandle GetEntityHandle(EntityIndex entityIndex)
        {
            return _entityLocator.GetEntityHandle(entityIndex);
        }

        // ── Component queries ───────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndexMapper<T> QueryMappedEntities<T>(GroupIndex groupStructId)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(groupStructId, out var typeSafeDictionary))
                throw new TrecsException(
                    $"entity group {groupStructId} not used for component type {typeof(T)}"
                );

            return (typeSafeDictionary as IComponentArray<T>).ToEntityIndexMapper(groupStructId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndexMultiMapper<T> QueryMappedEntities<T>(
            LocalReadOnlyFastList<GroupIndex> groups
        )
            where T : unmanaged, IEntityComponent
        {
            var dictionary = new DenseDictionary<GroupIndex, IComponentArray<T>>(groups.Count);

            foreach (var group in groups)
            {
                QueryOrCreateEntityDictionary<T>(group, out var typeSafeDictionary);
                dictionary.Add(group, typeSafeDictionary as IComponentArray<T>);
            }

            return new EntityIndexMultiMapper<T>(dictionary);
        }

        /// <summary>
        /// determine if component with specific ID exists in group
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists<T>(EntityIndex entityGID)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(entityGID.GroupIndex, out var casted))
                return false;

            return casted != null && entityGID.Index < casted.Count;
        }

        /// <summary>
        /// determine if component with specific ID exists in group
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists<T>(int id, GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(group, out var casted))
                return false;

            return casted != null && id < casted.Count;
        }

        /// <summary>
        /// count the number of components in a group
        /// </summary>
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
            var componentMap = _componentStore.GroupEntityComponentsDB[
                entityIndex.GroupIndex.Index
            ];

            foreach (var (_, componentArray) in componentMap)
            {
                return entityIndex.Index < componentArray.Count;
            }

            return false;
        }

        public int CountEntitiesInGroup(GroupIndex group)
        {
            Assert.That(
                !group.IsNull && group.Index < _componentStore.GroupEntityComponentsDB.Length,
                "Attempted to get count for unrecognized group {}",
                group
            );
            var entitiesInGroupPerType = _componentStore.GroupEntityComponentsDB[group.Index];

            // Zero-component templates (e.g. tag/filter-only) have no
            // component arrays, so we can't sample from one. They also can't
            // hold per-entity data, so the live count is always 0.
            if (entitiesInGroupPerType.Count == 0)
            {
                return 0;
            }

            int? count = null;

            foreach (var (key, value) in entitiesInGroupPerType)
            {
                if (count == null)
                {
                    count = value.Count;
#if !DEBUG
                    break;
#endif
                }
                else
                {
                    Assert.IsEqual(count, value.Count);
                }
            }

            return count.Value;
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
                    + "If you are accessing this data from within an OnRemoved callback, note that "
                    + "removed entities are moved past the active array count during submission and "
                    + "cannot be accessed via normal component queries. Use [ForEachEntity] on the "
                    + "callback method instead, which generates code that correctly accesses removed "
                    + "entity data.";
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
            LocalReadOnlyFastList<GroupIndex> groups,
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
                    var rawPtr = buffer.GetRawReadWritePointer(out _);
                    entries[writeIdx] = new NativeComponentLookupEntry
                    {
                        GroupIndex = group,
                        DataPtr = (void*)rawPtr,
                        Count = entryCount,
                    };
                    writeIdx++;
                }
            }

            count = writeIdx;
        }

        // ── Sets ────────────────────────────────────────────────────────

        public TrecsSets GetSets()
        {
            return _setStore.GetTrecsSets();
        }

        // ── Internal helpers ────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool SafeQueryEntityDictionary<T>(
            DenseDictionary<ComponentId, IComponentArray> entitiesInGroupPerType,
            out IComponentArray typeSafeDictionary
        )
            where T : unmanaged, IEntityComponent
        {
            if (
                !entitiesInGroupPerType.TryGetValue(
                    ComponentTypeId<T>.Value,
                    out var safeDictionary
                )
            )
            {
                typeSafeDictionary = default;
                return false;
            }

            //return the indexes entities if they exist
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
            var entitiesInGroupPerType = _componentStore.GroupEntityComponentsDB[group.Index];

            if (
                !entitiesInGroupPerType.TryGetValue(
                    ComponentTypeId<T>.Value,
                    out var safeDictionary
                )
            )
            {
                typeSafeDictionary = default;
                return false;
            }

            typeSafeDictionary = safeDictionary;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void QueryOrCreateEntityDictionary<T>(
            GroupIndex group,
            out IComponentArray typeSafeDictionary
        )
            where T : unmanaged, IEntityComponent
        {
            // Outer array is pre-populated at ComponentStore ctor; every GroupIndex
            // has an inner dict (initially empty).
            var entitiesInGroupPerType = _componentStore.GroupEntityComponentsDB[group.Index];

            var componentId = ComponentTypeId<T>.Value;

            if (!entitiesInGroupPerType.TryGetValue(componentId, out typeSafeDictionary))
            {
                Assert.That(!_componentStore.ConfigurationFrozen);

                typeSafeDictionary = new ComponentArray<T>(0);
                entitiesInGroupPerType.Add(componentId, typeSafeDictionary);
            }
        }

        internal NativeBuffer<T> QuerySingleBuffer<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            var entitiesInGroupPerType = _componentStore.GroupEntityComponentsDB[group.Index];

            if (!SafeQueryEntityDictionary<T>(entitiesInGroupPerType, out var typeSafeDictionary))
                return default;

            return ((IComponentArray<T>)typeSafeDictionary).GetValues(out _);
        }

        internal (NativeBuffer<T> buffer, int count) QuerySingleBufferWithCount<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            var entitiesInGroupPerType = _componentStore.GroupEntityComponentsDB[group.Index];

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
                failReason = QueryFailReason.IndexOutOfRange;
                return false;
            }

            buffer = (safeDictionary as IComponentArray<T>).GetValues(out _);

            return true;
        }

        bool GroupHasAllComponents(GroupIndex group, ComponentId[] componentIds)
        {
            var componentMap = _componentStore.GroupEntityComponentsDB[group.Index];
            for (int i = 0; i < componentIds.Length; i++)
            {
                if (!componentMap.TryGetValue(componentIds[i], out var arr) || arr.Count == 0)
                    return false;
            }
            return true;
        }

        IComponentArray GetComponentArrayUntyped(GroupIndex group, ComponentId componentId)
        {
            var componentMap = _componentStore.GroupEntityComponentsDB[group.Index];
            if (!componentMap.TryGetValue(componentId, out var arr))
                return null;
            return arr;
        }

        // ── Nested types ────────────────────────────────────────────────

        /// <summary>
        /// Provides read-only access to the set store for set lookups.
        /// </summary>
        public readonly struct TrecsSets
        {
            internal TrecsSets(NativeDenseDictionary<SetId, EntitySet> entitySets)
            {
                _entitySets = entitySets;
            }

            internal NativeDenseDictionary<SetId, EntitySet> EntitySets => _entitySets;

            readonly NativeDenseDictionary<SetId, EntitySet> _entitySets;
        }
    }
}
