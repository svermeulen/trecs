using System;
using Trecs.Collections;
using Unity.Collections;

namespace Trecs.Internal
{
    struct OtherComponentsToAddPerGroupEnumerator
    {
        public OtherComponentsToAddPerGroupEnumerator(
            DenseDictionary<
                GroupIndex,
                DenseDictionary<ComponentId, IComponentArray>
            > lastComponentsToAddPerGroup,
            DenseDictionary<GroupIndex, int> otherNumberEntitiesCreatedPerGroup
        )
        {
            _lastComponentsToAddPerGroup = lastComponentsToAddPerGroup;
            _lastNumberEntitiesCreatedPerGroup = otherNumberEntitiesCreatedPerGroup.GetEnumerator();
            Current = default;
        }

        public bool MoveNext()
        {
            while (_lastNumberEntitiesCreatedPerGroup.MoveNext())
            {
                var current = _lastNumberEntitiesCreatedPerGroup.Current;

                if (current.Value > 0) //there are entities in this group
                {
                    var value = _lastComponentsToAddPerGroup[current.Key];
                    Current = new GroupInfo() { GroupIndex = current.Key, Components = value };

                    return true;
                }
            }

            return false;
        }

        public GroupInfo Current { get; private set; }

        //cannot be read only as they will be modified by MoveNext
        readonly DenseDictionary<
            GroupIndex,
            DenseDictionary<ComponentId, IComponentArray>
        > _lastComponentsToAddPerGroup;

        DenseDictionary<GroupIndex, int>.Enumerator _lastNumberEntitiesCreatedPerGroup;
    }

    struct GroupInfo
    {
        public GroupIndex GroupIndex;
        public DenseDictionary<ComponentId, IComponentArray> Components;
    }

    internal class DoubleBufferedEntitiesToAdd
    {
        //while caching is good to avoid over creating dictionaries that may be reused, the side effect
        //is that I have to iterate every time up to 100 dictionaries during the flushing of the build entities
        //even if there are 0 entities inside.
        const int MAX_NUMBER_OF_GROUPS_TO_CACHE = 100;
        const int MAX_NUMBER_OF_TYPES_PER_GROUP_TO_CACHE = 100;

        public DoubleBufferedEntitiesToAdd()
        {
            var entitiesCreatedPerGroupA = new DenseDictionary<GroupIndex, int>();
            var entitiesCreatedPerGroupB = new DenseDictionary<GroupIndex, int>();
            var entityComponentsToAddBufferA =
                new DenseDictionary<GroupIndex, DenseDictionary<ComponentId, IComponentArray>>();
            var entityComponentsToAddBufferB =
                new DenseDictionary<GroupIndex, DenseDictionary<ComponentId, IComponentArray>>();

            _currentNumberEntitiesCreatedPerGroup = entitiesCreatedPerGroupA;
            _lastNumberEntitiesCreatedPerGroup = entitiesCreatedPerGroupB;

            currentComponentsToAddPerGroup = entityComponentsToAddBufferA;
            lastComponentsToAddPerGroup = entityComponentsToAddBufferB;

            _currentPendingReferences = new DenseDictionary<GroupIndex, FastList<EntityHandle>>();
            _lastPendingReferences = new DenseDictionary<GroupIndex, FastList<EntityHandle>>();

            _currentNativeAddSortKeys = new DenseDictionary<GroupIndex, FastList<ulong>>();
            _lastNativeAddSortKeys = new DenseDictionary<GroupIndex, FastList<ulong>>();
            _currentNativeAddStartIndices = new DenseDictionary<GroupIndex, int>();
            _lastNativeAddStartIndices = new DenseDictionary<GroupIndex, int>();
        }

        public void ClearLastAddOperations()
        {
            var numberOfGroupsAddedSoFar = lastComponentsToAddPerGroup.Count;
            var componentDictionariesPerType = lastComponentsToAddPerGroup.UnsafeValues;

            // Caching strategy: keep dictionaries alive for a reasonable number of groups
            // to avoid recreation cost on subsequent submissions. When too many groups
            // accumulate, dispose everything to avoid unbounded memory growth.

            //If we didn't create too many groups, we keep them alive, so we avoid the cost of creating new dictionaries
            //during future submissions, otherwise we clean up everything
            if (numberOfGroupsAddedSoFar > MAX_NUMBER_OF_GROUPS_TO_CACHE)
            {
                for (var i = 0; i < numberOfGroupsAddedSoFar; ++i)
                {
                    var componentTypesCount = componentDictionariesPerType[i].Count;
                    var componentTypesDictionary = componentDictionariesPerType[i].UnsafeValues;
                    {
                        for (var j = 0; j < componentTypesCount; ++j)
                            //dictionaries of components may be native so they need to be disposed
                            //before the references are GCed
                            componentTypesDictionary[j].Dispose();
                    }
                }

                //reset the number of entities created so far
                _lastNumberEntitiesCreatedPerGroup.Clear();
                lastComponentsToAddPerGroup.Clear();
                _lastPendingReferences.Recycle();
                _lastNativeAddSortKeys.Recycle();
                _lastNativeAddStartIndices.Clear();

                return;
            }

            for (var i = 0; i < numberOfGroupsAddedSoFar; ++i)
            {
                var componentTypesCount = componentDictionariesPerType[i].Count;
                IComponentArray[] componentTypesDictionary = componentDictionariesPerType[
                    i
                ].UnsafeValues;

                //if we didn't create too many component for this group, I reuse the component arrays
                if (componentTypesCount <= MAX_NUMBER_OF_TYPES_PER_GROUP_TO_CACHE)
                {
                    for (var j = 0; j < componentTypesCount; ++j)
                        //clear the dictionary of entities created so far (it won't allocate though)
                        componentTypesDictionary[j].Clear();
                }
                else
                {
                    //here I have to dispose, because I am actually clearing the reference of the dictionary
                    //with the next line.
                    for (var j = 0; j < componentTypesCount; ++j)
                        componentTypesDictionary[j].Dispose();

                    componentDictionariesPerType[i].Clear();
                }
            }

            //reset the number of entities created so far
            _lastNumberEntitiesCreatedPerGroup.Clear();
            _lastPendingReferences.Recycle();
            _lastNativeAddSortKeys.Recycle();
            _lastNativeAddStartIndices.Clear();
        }

        public void Dispose()
        {
            {
                var otherValuesArray = lastComponentsToAddPerGroup.UnsafeValues;
                for (var i = 0; i < lastComponentsToAddPerGroup.Count; ++i)
                {
                    int safeDictionariesCount = otherValuesArray[i].Count;
                    IComponentArray[] safeDictionaries = otherValuesArray[i].UnsafeValues;
                    //do not remove the dictionaries of entities per type created so far, they will be reused
                    for (var j = 0; j < safeDictionariesCount; ++j)
                        //clear the dictionary of entities create do far (it won't allocate though)
                        safeDictionaries[j].Dispose();
                }
            }
            {
                var currentValuesArray = currentComponentsToAddPerGroup.UnsafeValues;
                for (var i = 0; i < currentComponentsToAddPerGroup.Count; ++i)
                {
                    int safeDictionariesCount = currentValuesArray[i].Count;
                    IComponentArray[] safeDictionaries = currentValuesArray[i].UnsafeValues;
                    //do not remove the dictionaries of entities per type created so far, they will be reused
                    for (var j = 0; j < safeDictionariesCount; ++j)
                        //clear the dictionary of entities create do far (it won't allocate though)
                        safeDictionaries[j].Dispose();
                }
            }

            _currentNumberEntitiesCreatedPerGroup = null;
            _lastNumberEntitiesCreatedPerGroup = null;
            lastComponentsToAddPerGroup = null;
            currentComponentsToAddPerGroup = null;

            if (_cachedSortBuffer.IsCreated)
                _cachedSortBuffer.Dispose();
            if (_cachedSortIndices.IsCreated)
                _cachedSortIndices.Dispose();
            if (_cachedSortTempRefs.IsCreated)
                _cachedSortTempRefs.Dispose();
        }

        internal bool AnyEntityCreated()
        {
            return _currentNumberEntitiesCreatedPerGroup.Count > 0;
        }

        internal bool AnyPreviousEntityCreated()
        {
            return _lastNumberEntitiesCreatedPerGroup.Count > 0;
        }

        internal void IncrementEntityCount(GroupIndex groupId)
        {
            _currentNumberEntitiesCreatedPerGroup.GetOrAdd(groupId)++;
            //   _totalEntitiesToAdd++;
        }

        // public uint NumberOfEntitiesToAdd()
        // {
        //     return _totalEntitiesToAdd;
        // }

        public void FreezeConfiguration()
        {
            _configurationFrozen = true;
        }

        internal void Preallocate(
            GroupIndex groupId,
            int numberOfEntities,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
            // We could relax this constraint, since this method could be useful
            // at runtime if we know the number of entities we need
            // If so - change to check _configurationFrozen below instead, in
            // cases where new group/component dictionaries are added
            Assert.That(!_configurationFrozen);

            void PreallocateDictionaries(
                DenseDictionary<GroupIndex, DenseDictionary<ComponentId, IComponentArray>> dic
            )
            {
                //get the set of entities in the group ID
                var group = dic.GetOrAdd(
                    groupId,
                    () => new DenseDictionary<ComponentId, IComponentArray>()
                );

                //for each component of the entities in the group
                foreach (var componentBuilder in entityComponentsToBuild)
                {
                    //get the dictionary of entities for the component type
                    var components = group.GetOrAdd(
                        componentBuilder.ComponentId,
                        () => componentBuilder.CreateDictionary(numberOfEntities)
                    );

                    componentBuilder.Preallocate(components, numberOfEntities);
                }
            }

            PreallocateDictionaries(currentComponentsToAddPerGroup);
            PreallocateDictionaries(lastComponentsToAddPerGroup);

            _currentNumberEntitiesCreatedPerGroup.GetOrAdd(groupId);
            _lastNumberEntitiesCreatedPerGroup.GetOrAdd(groupId);
        }

        internal void Swap()
        {
            Swap(ref currentComponentsToAddPerGroup, ref lastComponentsToAddPerGroup);
            Swap(ref _currentNumberEntitiesCreatedPerGroup, ref _lastNumberEntitiesCreatedPerGroup);
            Swap(ref _currentPendingReferences, ref _lastPendingReferences);
            Swap(ref _currentNativeAddSortKeys, ref _lastNativeAddSortKeys);
            Swap(ref _currentNativeAddStartIndices, ref _lastNativeAddStartIndices);
        }

        static void Swap<T>(ref T item1, ref T item2)
        {
            (item2, item1) = (item1, item2);
        }

        public OtherComponentsToAddPerGroupEnumerator GetEnumerator()
        {
            return new OtherComponentsToAddPerGroupEnumerator(
                lastComponentsToAddPerGroup,
                _lastNumberEntitiesCreatedPerGroup
            );
        }

        //Before I tried for the third time to use a SparseSet instead of DenseDictionary, remember that
        //while group indices are sequential, they may not be used in a sequential order. Sparseset needs
        //entities to be created sequentially (the index cannot be managed externally)
        internal DenseDictionary<
            GroupIndex,
            DenseDictionary<ComponentId, IComponentArray>
        > currentComponentsToAddPerGroup;

        DenseDictionary<
            GroupIndex,
            DenseDictionary<ComponentId, IComponentArray>
        > lastComponentsToAddPerGroup;

        /// <summary>
        ///     To avoid extra allocation, I don't clear the groups, so I need an extra data structure
        ///     to keep count of the number of entities built this frame. At the moment the actual number
        ///     of entities built is not used
        /// </summary>
        DenseDictionary<GroupIndex, int> _currentNumberEntitiesCreatedPerGroup;
        DenseDictionary<GroupIndex, int> _lastNumberEntitiesCreatedPerGroup;

        // Track EntityHandle references for entities being added, so we can
        // call SetEntityHandle with the correct DB index during submission
        internal DenseDictionary<GroupIndex, FastList<EntityHandle>> _currentPendingReferences;
        DenseDictionary<GroupIndex, FastList<EntityHandle>> _lastPendingReferences;

        static readonly Func<FastList<EntityHandle>> _newPendingRefList = () =>
            new FastList<EntityHandle>();
        static readonly ActionRef<FastList<EntityHandle>> _clearPendingRefList = (
            ref FastList<EntityHandle> l
        ) => l.Clear();

        internal void AddPendingReference(GroupIndex group, EntityHandle reference)
        {
            var list = _currentPendingReferences.RecycleOrAdd(
                group,
                _newPendingRefList,
                _clearPendingRefList
            );
            list.Add(reference);
        }

        internal DenseDictionary<GroupIndex, FastList<EntityHandle>> LastPendingReferences =>
            _lastPendingReferences;

        // Track composite sort keys for native adds (packed as (ulong)accessorId << 32 | sortKey)
        DenseDictionary<GroupIndex, FastList<ulong>> _currentNativeAddSortKeys;
        DenseDictionary<GroupIndex, FastList<ulong>> _lastNativeAddSortKeys;

        // Track where native adds start in each group's component arrays
        DenseDictionary<GroupIndex, int> _currentNativeAddStartIndices;
        DenseDictionary<GroupIndex, int> _lastNativeAddStartIndices;

        static readonly Func<FastList<ulong>> _newSortKeyList = () => new FastList<ulong>();
        static readonly ActionRef<FastList<ulong>> _clearSortKeyList = (ref FastList<ulong> l) =>
            l.Clear();

        internal void AddPendingNativeAddSortKey(GroupIndex group, int accessorId, uint sortKey)
        {
            var list = _currentNativeAddSortKeys.RecycleOrAdd(
                group,
                _newSortKeyList,
                _clearSortKeyList
            );
            ulong compositeKey = ((ulong)(uint)accessorId << 32) | sortKey;
            list.Add(compositeKey);
        }

        internal void MarkNativeAddStartIfNeeded(GroupIndex group, int currentArrayCount)
        {
            if (!_currentNativeAddStartIndices.ContainsKey(group))
            {
                _currentNativeAddStartIndices.Add(group, currentArrayCount);
            }
        }

        // Cached native lists for SortNativeAdds to avoid per-frame allocations
        NativeList<KeyedIndex> _cachedSortBuffer;
        NativeList<int> _cachedSortIndices;
        NativeList<EntityHandle> _cachedSortTempRefs;

        struct KeyedIndex : IComparable<KeyedIndex>
        {
            public ulong Key;
            public int Index;

            public int CompareTo(KeyedIndex other) => Key.CompareTo(other.Key);
        }

        /// <summary>
        /// Sort native adds within each group by composite sort key (accessorId, sortKey).
        /// Applies the resulting permutation to all component arrays and pending references.
        /// </summary>
        internal void SortNativeAdds()
        {
            var sortKeysCount = _currentNativeAddSortKeys.Count;
            if (sortKeysCount == 0)
            {
                return;
            }

            if (!_cachedSortBuffer.IsCreated)
            {
                _cachedSortBuffer = new NativeList<KeyedIndex>(16, Allocator.Persistent);
                _cachedSortIndices = new NativeList<int>(16, Allocator.Persistent);
                _cachedSortTempRefs = new NativeList<EntityHandle>(16, Allocator.Persistent);
            }

            var sortKeysNodes = _currentNativeAddSortKeys.UnsafeKeys;
            var sortKeysValues = _currentNativeAddSortKeys.UnsafeValues;

            for (int gi = 0; gi < sortKeysCount; gi++)
            {
                var group = sortKeysNodes[gi].key;
                var keys = sortKeysValues[gi];
                var count = keys.Count;

                if (count <= 1)
                {
                    continue;
                }

                var startIndex = _currentNativeAddStartIndices[group];

                // Build sortable key+index pairs
                _cachedSortBuffer.Clear();
                for (int i = 0; i < count; i++)
                {
                    _cachedSortBuffer.Add(new KeyedIndex { Key = keys[i], Index = i });
                }

                // Native sort (non-allocating)
                _cachedSortBuffer.Sort();

                // Check for duplicates (adjacent after sort)
                for (int i = 1; i < count; i++)
                {
                    Assert.That(
                        _cachedSortBuffer[i].Key != _cachedSortBuffer[i - 1].Key,
                        "Duplicate native add sort key detected in group {} (composite key {}). "
                            + "Each system must use unique sort keys for adds to the same group.",
                        group,
                        _cachedSortBuffer[i].Key
                    );
                }

                // Check if already in order — skip permutation if so
                bool alreadySorted = true;
                for (int i = 0; i < count; i++)
                {
                    if (_cachedSortBuffer[i].Index != i)
                    {
                        alreadySorted = false;
                        break;
                    }
                }

                if (alreadySorted)
                {
                    continue;
                }

                // Extract sorted indices for permutation
                _cachedSortIndices.Clear();
                for (int i = 0; i < count; i++)
                {
                    _cachedSortIndices.Add(_cachedSortBuffer[i].Index);
                }

                // Apply permutation to all component arrays for this group
                var groupDict = currentComponentsToAddPerGroup[group];
                var componentArrays = groupDict.UnsafeValues;
                var componentCount = groupDict.Count;

                for (int ci = 0; ci < componentCount; ci++)
                {
                    componentArrays[ci].ReorderRange(startIndex, count, _cachedSortIndices);
                }

                // Apply permutation to pending references
                _cachedSortTempRefs.Clear();
                var refs = _currentPendingReferences[group];
                for (int i = 0; i < count; i++)
                {
                    _cachedSortTempRefs.Add(refs[startIndex + _cachedSortIndices[i]]);
                }

                for (int i = 0; i < count; i++)
                {
                    refs[startIndex + i] = _cachedSortTempRefs[i];
                }
            }
        }

        bool _configurationFrozen;

        //uint _totalEntitiesToAdd;
    }
}
