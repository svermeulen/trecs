using System;
using Trecs.Collections;
using Unity.Collections;

namespace Trecs.Internal
{
    struct OtherComponentsToAddPerGroupEnumerator
    {
        int _index;
        readonly int _length;
        readonly int[] _counts;
        readonly DenseDictionary<ComponentId, IComponentArray>[] _components;

        public OtherComponentsToAddPerGroupEnumerator(
            DenseDictionary<ComponentId, IComponentArray>[] lastComponentsToAddPerGroup,
            int[] lastNumberEntitiesCreatedPerGroup
        )
        {
            _components = lastComponentsToAddPerGroup;
            _counts = lastNumberEntitiesCreatedPerGroup;
            _length = _counts.Length;
            _index = -1;
            Current = default;
        }

        public bool MoveNext()
        {
            while (++_index < _length)
            {
                if (_counts[_index] > 0)
                {
                    Current = new GroupInfo
                    {
                        GroupIndex = GroupIndex.FromIndex(_index),
                        Components = _components[_index],
                    };
                    return true;
                }
            }
            return false;
        }

        public GroupInfo Current { get; private set; }
    }

    struct GroupInfo
    {
        public GroupIndex GroupIndex;
        public DenseDictionary<ComponentId, IComponentArray> Components;
    }

    internal class DoubleBufferedEntitiesToAdd
    {
        public DoubleBufferedEntitiesToAdd(int groupCount)
        {
            currentComponentsToAddPerGroup = new DenseDictionary<ComponentId, IComponentArray>[
                groupCount
            ];
            lastComponentsToAddPerGroup = new DenseDictionary<ComponentId, IComponentArray>[
                groupCount
            ];

            _currentNumberEntitiesCreatedPerGroup = new int[groupCount];
            _lastNumberEntitiesCreatedPerGroup = new int[groupCount];

            _currentPendingReferences = new FastList<EntityHandle>[groupCount];
            _lastPendingReferences = new FastList<EntityHandle>[groupCount];

            _currentNativeAddSortKeys = new FastList<ulong>[groupCount];
            _lastNativeAddSortKeys = new FastList<ulong>[groupCount];

            _currentNativeAddStartIndices = new int[groupCount];
            _lastNativeAddStartIndices = new int[groupCount];
            Array.Fill(_currentNativeAddStartIndices, -1);
            Array.Fill(_lastNativeAddStartIndices, -1);
        }

        public void ClearLastAddOperations()
        {
            // Reuse IComponentArrays by clearing in place — retained allocations
            // are bounded by template-defined component counts (fixed at config).
            for (int i = 0; i < lastComponentsToAddPerGroup.Length; i++)
            {
                var inner = lastComponentsToAddPerGroup[i];
                if (inner == null || inner.Count == 0)
                    continue;

                var componentTypesCount = inner.Count;
                var componentTypesDictionary = inner.UnsafeValues;
                for (int j = 0; j < componentTypesCount; j++)
                    componentTypesDictionary[j].Clear();
            }

            Array.Clear(
                _lastNumberEntitiesCreatedPerGroup,
                0,
                _lastNumberEntitiesCreatedPerGroup.Length
            );
            _totalEntitiesCreatedLastFrame = 0;

            for (int i = 0; i < _lastPendingReferences.Length; i++)
                _lastPendingReferences[i]?.Clear();

            for (int i = 0; i < _lastNativeAddSortKeys.Length; i++)
                _lastNativeAddSortKeys[i]?.Clear();

            Array.Fill(_lastNativeAddStartIndices, -1);
        }

        public void Dispose()
        {
            DisposeBuffer(lastComponentsToAddPerGroup);
            DisposeBuffer(currentComponentsToAddPerGroup);

            currentComponentsToAddPerGroup = null;
            lastComponentsToAddPerGroup = null;
            _currentNumberEntitiesCreatedPerGroup = null;
            _lastNumberEntitiesCreatedPerGroup = null;

            if (_cachedSortBuffer.IsCreated)
                _cachedSortBuffer.Dispose();
            if (_cachedSortIndices.IsCreated)
                _cachedSortIndices.Dispose();
            if (_cachedSortTempRefs.IsCreated)
                _cachedSortTempRefs.Dispose();
        }

        static void DisposeBuffer(DenseDictionary<ComponentId, IComponentArray>[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                var inner = buffer[i];
                if (inner == null)
                    continue;
                var count = inner.Count;
                var values = inner.UnsafeValues;
                for (int j = 0; j < count; j++)
                    values[j].Dispose();
            }
        }

        internal bool AnyEntityCreated() => _totalEntitiesCreatedThisFrame > 0;

        internal bool AnyPreviousEntityCreated() => _totalEntitiesCreatedLastFrame > 0;

        internal void IncrementEntityCount(GroupIndex groupId)
        {
            _currentNumberEntitiesCreatedPerGroup[groupId.Index]++;
            _totalEntitiesCreatedThisFrame++;
        }

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

            PreallocateDictionaries(
                currentComponentsToAddPerGroup,
                groupId,
                numberOfEntities,
                entityComponentsToBuild
            );
            PreallocateDictionaries(
                lastComponentsToAddPerGroup,
                groupId,
                numberOfEntities,
                entityComponentsToBuild
            );
        }

        static void PreallocateDictionaries(
            DenseDictionary<ComponentId, IComponentArray>[] buffer,
            GroupIndex groupId,
            int numberOfEntities,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
            ref var group = ref buffer[groupId.Index];
            group ??= new DenseDictionary<ComponentId, IComponentArray>();

            foreach (var componentBuilder in entityComponentsToBuild)
            {
                var components = group.GetOrAdd(
                    componentBuilder.ComponentId,
                    () => componentBuilder.CreateDictionary(numberOfEntities)
                );
                componentBuilder.Preallocate(components, numberOfEntities);
            }
        }

        internal void Swap()
        {
            (currentComponentsToAddPerGroup, lastComponentsToAddPerGroup) = (
                lastComponentsToAddPerGroup,
                currentComponentsToAddPerGroup
            );
            (_currentNumberEntitiesCreatedPerGroup, _lastNumberEntitiesCreatedPerGroup) = (
                _lastNumberEntitiesCreatedPerGroup,
                _currentNumberEntitiesCreatedPerGroup
            );
            (_totalEntitiesCreatedThisFrame, _totalEntitiesCreatedLastFrame) = (
                _totalEntitiesCreatedLastFrame,
                _totalEntitiesCreatedThisFrame
            );
            (_currentPendingReferences, _lastPendingReferences) = (
                _lastPendingReferences,
                _currentPendingReferences
            );
            (_currentNativeAddSortKeys, _lastNativeAddSortKeys) = (
                _lastNativeAddSortKeys,
                _currentNativeAddSortKeys
            );
            (_currentNativeAddStartIndices, _lastNativeAddStartIndices) = (
                _lastNativeAddStartIndices,
                _currentNativeAddStartIndices
            );
        }

        public OtherComponentsToAddPerGroupEnumerator GetEnumerator()
        {
            return new OtherComponentsToAddPerGroupEnumerator(
                lastComponentsToAddPerGroup,
                _lastNumberEntitiesCreatedPerGroup
            );
        }

        internal DenseDictionary<ComponentId, IComponentArray> GetOrCreateCurrentComponentsForGroup(
            GroupIndex group
        )
        {
            ref var slot = ref currentComponentsToAddPerGroup[group.Index];
            return slot ??= new DenseDictionary<ComponentId, IComponentArray>();
        }

        internal bool TryGetLastPendingReferences(
            GroupIndex group,
            out FastList<EntityHandle> pendingRefs
        )
        {
            pendingRefs = _lastPendingReferences[group.Index];
            return pendingRefs != null && pendingRefs.Count > 0;
        }

        internal void AddPendingReference(GroupIndex group, EntityHandle reference)
        {
            ref var slot = ref _currentPendingReferences[group.Index];
            slot ??= new FastList<EntityHandle>();
            slot.Add(reference);
        }

        internal void AddPendingNativeAddSortKey(GroupIndex group, int accessorId, uint sortKey)
        {
            ref var slot = ref _currentNativeAddSortKeys[group.Index];
            slot ??= new FastList<ulong>();
            ulong compositeKey = ((ulong)(uint)accessorId << 32) | sortKey;
            slot.Add(compositeKey);
        }

        internal void MarkNativeAddStartIfNeeded(GroupIndex group, int currentArrayCount)
        {
            if (_currentNativeAddStartIndices[group.Index] < 0)
            {
                _currentNativeAddStartIndices[group.Index] = currentArrayCount;
            }
        }

        internal DenseDictionary<ComponentId, IComponentArray>[] currentComponentsToAddPerGroup;

        DenseDictionary<ComponentId, IComponentArray>[] lastComponentsToAddPerGroup;

        int[] _currentNumberEntitiesCreatedPerGroup;
        int[] _lastNumberEntitiesCreatedPerGroup;

        int _totalEntitiesCreatedThisFrame;
        int _totalEntitiesCreatedLastFrame;

        FastList<EntityHandle>[] _currentPendingReferences;
        FastList<EntityHandle>[] _lastPendingReferences;

        FastList<ulong>[] _currentNativeAddSortKeys;
        FastList<ulong>[] _lastNativeAddSortKeys;

        int[] _currentNativeAddStartIndices;
        int[] _lastNativeAddStartIndices;

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
            if (!_cachedSortBuffer.IsCreated)
            {
                _cachedSortBuffer = new NativeList<KeyedIndex>(16, Allocator.Persistent);
                _cachedSortIndices = new NativeList<int>(16, Allocator.Persistent);
                _cachedSortTempRefs = new NativeList<EntityHandle>(16, Allocator.Persistent);
            }

            for (int gi = 0; gi < _currentNativeAddSortKeys.Length; gi++)
            {
                var keys = _currentNativeAddSortKeys[gi];
                if (keys == null || keys.Count <= 1)
                {
                    continue;
                }

                var group = GroupIndex.FromIndex(gi);
                var count = keys.Count;
                var startIndex = _currentNativeAddStartIndices[gi];
                Assert.That(
                    startIndex >= 0,
                    "Native add start index not set for group {} despite non-empty sort keys. "
                        + "MarkNativeAddStartIfNeeded must be called before AddPendingNativeAddSortKey.",
                    group
                );

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
                var groupDict = currentComponentsToAddPerGroup[gi];
                var componentArrays = groupDict.UnsafeValues;
                var componentCount = groupDict.Count;

                for (int ci = 0; ci < componentCount; ci++)
                {
                    componentArrays[ci].ReorderRange(startIndex, count, _cachedSortIndices);
                }

                // Apply permutation to pending references
                _cachedSortTempRefs.Clear();
                var refs = _currentPendingReferences[gi];
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
    }
}
