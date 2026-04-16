using System.Runtime.CompilerServices;
using System.Threading;
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

        internal EntityQuerier(ComponentStore componentStore, SetStore setStore)
        {
            _entityLocator.InitEntityHandleMap();

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
        public EntityIndexMapper<T> QueryMappedEntities<T>(Group groupStructId)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(groupStructId, out var typeSafeDictionary))
                throw new TrecsException(
                    $"entity group {groupStructId} not used for component type {typeof(T)}"
                );

            return (typeSafeDictionary as IComponentArray<T>).ToEntityIndexMapper(groupStructId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityIndexMultiMapper<T> QueryMappedEntities<T>(LocalReadOnlyFastList<Group> groups)
            where T : unmanaged, IEntityComponent
        {
            var dictionary = new DenseDictionary<Group, IComponentArray<T>>(groups.Count);

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
            if (!SafeQueryEntityDictionary<T>(entityGID.Group, out var casted))
                return false;

            return casted != null && entityGID.Index < casted.Count;
        }

        /// <summary>
        /// determine if component with specific ID exists in group
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists<T>(int id, Group group)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(group, out var casted))
                return false;

            return casted != null && id < casted.Count;
        }

        /// <summary>
        /// determine if group exists and is not empty
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ExistsAndIsNotEmpty(Group gid)
        {
            if (
                _componentStore.GroupEntityComponentsDB.TryGetValue(
                    gid,
                    out DenseDictionary<ComponentId, IComponentArray> group
                )
            )
            {
                return group.Count > 0;
            }

            return false;
        }

        /// <summary>
        /// determine if entities we specific components are found in group
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAny<T>(Group groupStruct)
            where T : unmanaged, IEntityComponent
        {
            return Count<T>(groupStruct) > 0;
        }

        /// <summary>
        /// count the number of components in a group
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Count<T>(Group groupStruct)
            where T : unmanaged, IEntityComponent
        {
            if (!SafeQueryEntityDictionary<T>(groupStruct, out var typeSafeDictionary))
                return 0;

            return typeSafeDictionary.Count;
        }

        public bool FoundInGroups<T>()
            where T : unmanaged, IEntityComponent
        {
            return _componentStore.GroupsPerComponent.ContainsKey(ComponentTypeId<T>.Value);
        }

        /// <summary>
        /// Returns true if the given <see cref="EntityIndex"/> points to valid data in the
        /// component database (i.e. the entity has been submitted).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EntityIndexExists(EntityIndex entityIndex)
        {
            if (
                !_componentStore.GroupEntityComponentsDB.TryGetValue(
                    entityIndex.Group,
                    out var componentMap
                )
            )
                return false;

            foreach (var (_, componentArray) in componentMap)
            {
                return entityIndex.Index < componentArray.Count;
            }

            return false;
        }

        public int CountEntitiesInGroup(Group group)
        {
            if (
                !_componentStore.GroupEntityComponentsDB.TryGetValue(
                    group,
                    out var entitiesInGroupPerType
                )
            )
            {
                throw Assert.CreateException(
                    "Attempted to get count for unrecognized group {}",
                    group
                );
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

            Assert.That(count.HasValue);
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
        public NativeBuffer<T> QueryEntitiesAndIndex<T>(int id, Group group, out int index)
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
            Group group,
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
                $"Entity with index '{entityGID.Index}', group '{entityGID.Group}' and component '{typeof(T)}'";

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
        public bool TryGetEntity<T>(int entityHandle, Group @group, out T value)
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
            return TryGetEntity<T>(entityIndex.Index, entityIndex.Group, out value);
        }

        /// <summary>
        /// Expects that only one entity of type T exists in the group
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T QueryUniqueEntity<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            var (buffer, count) = QuerySingleBufferWithCount<T>(group);

            Require.That(count != 0, "Unique entity not found '{}'", typeof(T));
            Require.That(count == 1, "Unique entities must be unique! '{}'", typeof(T));
            return ref buffer[0];
        }

        // ── FindGroups ──────────────────────────────────────────────────

        /// <summary>
        /// Return all the groups where the entity component is found. It's a linear operation, but usually the number of groups are very low
        /// </summary>
        public LocalReadOnlyFastList<Group> FindGroups<T1>()
            where T1 : unmanaged, IEntityComponent
        {
            FastList<Group> result = localgroups.Value.GroupArray;
            result.Clear();
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T1>.Value,
                    out DenseDictionary<Group, IComponentArray> result1
                )
            )
                return result;

            var result1Count = result1.Count;
            var fasterDictionaryNodes1 = result1.UnsafeKeys;

            for (int j = 0; j < result1Count; j++)
            {
                result.Add(fasterDictionaryNodes1[j].key);
            }

            return result;
        }

        public LocalReadOnlyFastList<Group> FindGroups<T1, T2>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
        {
            FastList<Group> result = localgroups.Value.GroupArray;
            result.Clear();
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T1>.Value,
                    out DenseDictionary<Group, IComponentArray> result1
                )
            )
                return result;
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T2>.Value,
                    out DenseDictionary<Group, IComponentArray> result2
                )
            )
                return result;

            var result1Count = result1.Count;
            var result2Count = result2.Count;
            var fasterDictionaryNodes1 = result1.UnsafeKeys;
            var fasterDictionaryNodes2 = result2.UnsafeKeys;

            for (int i = 0; i < result1Count; i++)
            {
                var groupId = fasterDictionaryNodes1[i].key;

                for (int j = 0; j < result2Count; j++)
                {
                    //if the same group is found used with both T1 and T2
                    if (groupId == fasterDictionaryNodes2[j].key)
                    {
                        result.Add(groupId);
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Remember that this operation is O(N*M*P) where N,M,P are the number of groups where each component
        /// is found.
        /// </summary>
        /// <typeparam name="T1"></typeparam>
        /// <typeparam name="T2"></typeparam>
        /// <typeparam name="T3"></typeparam>
        /// <returns></returns>
        public LocalReadOnlyFastList<Group> FindGroups<T1, T2, T3>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
        {
            FastList<DenseDictionary<Group, IComponentArray>> localArray = localgroups
                .Value
                .listOfGroups;

            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T1>.Value,
                    out localArray[0]
                )
                || localArray[0].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T2>.Value,
                    out localArray[1]
                )
                || localArray[1].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T3>.Value,
                    out localArray[2]
                )
                || localArray[2].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);

            localgroups.Value.groups.Clear();

            DenseDictionary<Group, Group> localGroups = localgroups.Value.groups;

            int startIndex = 0;
            int min = int.MaxValue;

            for (int i = 0; i < 3; i++)
                if (localArray[i].Count < min)
                {
                    min = localArray[i].Count;
                    startIndex = i;
                }

            foreach (var value in localArray[startIndex])
            {
                localGroups.Add(value.Key, value.Key);
            }

            var groupData = localArray[++startIndex % 3];
            localGroups.Intersect(groupData);
            if (localGroups.Count == 0)
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);

            groupData = localArray[++startIndex % 3];
            localGroups.Intersect(groupData);

            return new LocalReadOnlyFastList<Group>(localGroups.UnsafeValues, localGroups.Count);
        }

        public LocalReadOnlyFastList<Group> FindGroups<T1, T2, T3, T4>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
        {
            FastList<DenseDictionary<Group, IComponentArray>> localArray = localgroups
                .Value
                .listOfGroups;

            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T1>.Value,
                    out localArray[0]
                )
                || localArray[0].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T2>.Value,
                    out localArray[1]
                )
                || localArray[1].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T3>.Value,
                    out localArray[2]
                )
                || localArray[2].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T4>.Value,
                    out localArray[3]
                )
                || localArray[3].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);

            localgroups.Value.groups.Clear();

            var localGroups = localgroups.Value.groups;

            int startIndex = 0;
            int min = int.MaxValue;

            for (int i = 0; i < 4; i++)
            {
                var fasterDictionary = localArray[i];
                if (fasterDictionary.Count < min)
                {
                    min = fasterDictionary.Count;
                    startIndex = i;
                }
            }

            foreach (var value in localArray[startIndex])
            {
                localGroups.Add(value.Key, value.Key);
            }

            var groupData = localArray[++startIndex & 3]; //&3 == %4
            localGroups.Intersect(groupData);
            if (localGroups.Count == 0)
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);

            groupData = localArray[++startIndex & 3];
            localGroups.Intersect(groupData);
            if (localGroups.Count == 0)
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);

            groupData = localArray[++startIndex & 3];
            localGroups.Intersect(groupData);

            return new LocalReadOnlyFastList<Group>(localGroups.UnsafeValues, localGroups.Count);
        }

        public LocalReadOnlyFastList<Group> FindGroups<T1, T2, T3, T4, T5>()
            where T1 : unmanaged, IEntityComponent
            where T2 : unmanaged, IEntityComponent
            where T3 : unmanaged, IEntityComponent
            where T4 : unmanaged, IEntityComponent
            where T5 : unmanaged, IEntityComponent
        {
            FastList<DenseDictionary<Group, IComponentArray>> localArray = localgroups
                .Value
                .listOfGroups;

            // Check for all five component types
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T1>.Value,
                    out localArray[0]
                )
                || localArray[0].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T2>.Value,
                    out localArray[1]
                )
                || localArray[1].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T3>.Value,
                    out localArray[2]
                )
                || localArray[2].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T4>.Value,
                    out localArray[3]
                )
                || localArray[3].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);
            if (
                !_componentStore.GroupsPerComponent.TryGetValue(
                    ComponentTypeId<T5>.Value,
                    out localArray[4]
                )
                || localArray[4].Count == 0
            )
                return new LocalReadOnlyFastList<Group>(ReadOnlyFastList<Group>.DefaultEmptyList);

            localgroups.Value.groups.Clear();

            var localGroups = localgroups.Value.groups;

            int startIndex = 0;
            int min = int.MaxValue;

            // Find the component type with the smallest number of groups
            for (int i = 0; i < 5; i++)
            {
                var fasterDictionary = localArray[i];
                if (fasterDictionary.Count < min)
                {
                    min = fasterDictionary.Count;
                    startIndex = i;
                }
            }

            // Initialize localGroups with groups from the component type with the smallest count
            foreach (var value in localArray[startIndex])
            {
                localGroups.Add(value.Key, value.Key);
            }

            // Intersect with the remaining component types
            for (int i = 1; i < 5; i++)
            {
                var groupData = localArray[(startIndex + i) % 5];
                localGroups.Intersect(groupData);
                if (localGroups.Count == 0)
                    return new LocalReadOnlyFastList<Group>(
                        ReadOnlyFastList<Group>.DefaultEmptyList
                    );
            }

            return new LocalReadOnlyFastList<Group>(localGroups.UnsafeValues, localGroups.Count);
        }

        internal DenseDictionary<Group, IComponentArray> FindGroups_INTERNAL(ComponentId type)
        {
            if (!_componentStore.GroupsPerComponent.ContainsKey(type))
            {
                return EmptyDictionary;
            }

            return _componentStore.GroupsPerComponent[type];
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
            LocalReadOnlyFastList<Group> groups,
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
                        GroupId = group.Id,
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
            Group group,
            out IComponentArray typeSafeDictionary
        )
            where T : unmanaged, IEntityComponent
        {
            IComponentArray safeDictionary;
            bool ret;
            //search for the group
            if (
                !_componentStore.GroupEntityComponentsDB.TryGetValue(
                    group,
                    out DenseDictionary<ComponentId, IComponentArray> entitiesInGroupPerType
                )
            )
            {
                safeDictionary = null;
                ret = false;
            }
            else
            {
                ret = entitiesInGroupPerType.TryGetValue(
                    ComponentTypeId<T>.Value,
                    out safeDictionary
                );
            }

            //search for the indexed entities in the group
            if (!ret)
            {
                typeSafeDictionary = default;
                return false;
            }

            //return the indexes entities if they exist
            typeSafeDictionary = safeDictionary;

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void QueryOrCreateEntityDictionary<T>(
            Group group,
            out IComponentArray typeSafeDictionary
        )
            where T : unmanaged, IEntityComponent
        {
            //search for the group
            DenseDictionary<ComponentId, IComponentArray> entitiesInGroupPerType =
                _componentStore.GroupEntityComponentsDB.GetOrAdd(
                    group,
                    () => new DenseDictionary<ComponentId, IComponentArray>()
                );

            var componentId = ComponentTypeId<T>.Value;

            if (!entitiesInGroupPerType.TryGetValue(componentId, out typeSafeDictionary))
            {
                Assert.That(!_componentStore.ConfigurationFrozen);

                typeSafeDictionary = new ComponentArray<T>(0);
                entitiesInGroupPerType.Add(componentId, typeSafeDictionary);
            }
        }

        internal NativeBuffer<T> QuerySingleBuffer<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            if (
                !_componentStore.GroupEntityComponentsDB.TryGetValue(
                    group,
                    out var entitiesInGroupPerType
                )
            )
                return default;

            if (!SafeQueryEntityDictionary<T>(entitiesInGroupPerType, out var typeSafeDictionary))
                return default;

            return ((IComponentArray<T>)typeSafeDictionary).GetValues(out _);
        }

        internal (NativeBuffer<T> buffer, int count) QuerySingleBufferWithCount<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            if (
                !_componentStore.GroupEntityComponentsDB.TryGetValue(
                    group,
                    out var entitiesInGroupPerType
                )
            )
                return (default, 0);

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

            if (!SafeQueryEntityDictionary<T>(entityGID.Group, out var safeDictionary))
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

        bool GroupHasAllComponents(Group group, ComponentId[] componentIds)
        {
            if (!_componentStore.GroupEntityComponentsDB.TryGetValue(group, out var componentMap))
                return false;
            for (int i = 0; i < componentIds.Length; i++)
            {
                if (!componentMap.TryGetValue(componentIds[i], out var arr) || arr.Count == 0)
                    return false;
            }
            return true;
        }

        IComponentArray GetComponentArrayUntyped(Group group, ComponentId componentId)
        {
            if (!_componentStore.GroupEntityComponentsDB.TryGetValue(group, out var componentMap))
                return null;
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

        struct GroupsList
        {
            internal DenseDictionary<Group, Group> groups;
            internal FastList<DenseDictionary<Group, IComponentArray>> listOfGroups;
            public FastList<Group> GroupArray;
        }

        static readonly ThreadLocal<GroupsList> localgroups = new ThreadLocal<GroupsList>(() =>
        {
            GroupsList gl = default;

            gl.groups = new DenseDictionary<Group, Group>();
            gl.listOfGroups = FastList<DenseDictionary<Group, IComponentArray>>.PreInit(5);
            gl.GroupArray = new FastList<Group>(1);

            return gl;
        });

        static readonly DenseDictionary<Group, IComponentArray> EmptyDictionary = new();
    }
}
