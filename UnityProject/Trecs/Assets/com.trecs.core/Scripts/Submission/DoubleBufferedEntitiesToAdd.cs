using System;
using System.Collections.Generic;
using Trecs.Collections;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Trecs.Internal
{
    struct OtherComponentsToAddPerGroupEnumerator
    {
        int _index;
        readonly int _length;
        readonly int[] _counts;
        readonly IterableDictionary<TypeId, IComponentArray>[] _components;

        public OtherComponentsToAddPerGroupEnumerator(
            IterableDictionary<TypeId, IComponentArray>[] lastComponentsToAddPerGroup,
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
        public IterableDictionary<TypeId, IComponentArray> Components;
    }

    internal class DoubleBufferedEntitiesToAdd
    {
        public DoubleBufferedEntitiesToAdd(int groupCount)
        {
            currentComponentsToAddPerGroup = new IterableDictionary<TypeId, IComponentArray>[
                groupCount
            ];
            lastComponentsToAddPerGroup = new IterableDictionary<TypeId, IComponentArray>[
                groupCount
            ];

            _currentNumberEntitiesCreatedPerGroup = new int[groupCount];
            _lastNumberEntitiesCreatedPerGroup = new int[groupCount];

            _currentPendingReferences = new List<EntityHandle>[groupCount];
            _lastPendingReferences = new List<EntityHandle>[groupCount];

            _currentNativeAddSortKeys = new List<ulong>[groupCount];
            _lastNativeAddSortKeys = new List<ulong>[groupCount];

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
            if (_cachedReorderScratch.IsCreated)
                _cachedReorderScratch.Dispose();
        }

        static void DisposeBuffer(IterableDictionary<TypeId, IComponentArray>[] buffer)
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

        // Reserved hook for post-Init configuration-change asserts. Mirrors
        // ComponentStore.ConfigurationFrozen, which EntityQuerier reads to
        // guard against late reconfiguration. No live readers yet here; left
        // wired so future add-config-shaped methods can assert against it
        // without having to re-introduce the plumbing.
        public bool ConfigurationFrozen => _configurationFrozen;

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
            // Lazy by default; this entry point exists for callers that want to
            // eagerly reserve buffers for a group ahead of a known burst of adds
            // (see WorldAccessor.Warmup). Safe post-freeze — the underlying
            // PreallocateDictionaries / GetOrAdd paths are idempotent.

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
            IterableDictionary<TypeId, IComponentArray>[] buffer,
            GroupIndex groupId,
            int numberOfEntities,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
            ref var group = ref buffer[groupId.Index];
            group ??= new IterableDictionary<TypeId, IComponentArray>();

            foreach (var componentBuilder in entityComponentsToBuild)
            {
                var components = group.GetOrAdd(
                    componentBuilder.TypeId,
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

        internal IterableDictionary<TypeId, IComponentArray> GetOrCreateCurrentComponentsForGroup(
            GroupIndex group
        )
        {
            ref var slot = ref currentComponentsToAddPerGroup[group.Index];
            return slot ??= new IterableDictionary<TypeId, IComponentArray>();
        }

        internal bool TryGetLastPendingReferences(
            GroupIndex group,
            out List<EntityHandle> pendingRefs
        )
        {
            pendingRefs = _lastPendingReferences[group.Index];
            return pendingRefs != null && pendingRefs.Count > 0;
        }

        internal void AddPendingReference(GroupIndex group, EntityHandle reference)
        {
            ref var slot = ref _currentPendingReferences[group.Index];
            slot ??= new List<EntityHandle>();
            slot.Add(reference);
        }

        internal void AddPendingNativeAddSortKey(GroupIndex group, int accessorId, uint sortKey)
        {
            ref var slot = ref _currentNativeAddSortKeys[group.Index];
            slot ??= new List<ulong>();
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

        /// <summary>
        /// Walks the native-add slice of each group's pending-references list and
        /// claims an <see cref="EntityHandle"/> for any slot still set to
        /// <see cref="EntityHandle.Null"/> — i.e. queued by the void / handleless
        /// AddEntity overloads, which deliberately defer id claiming to the main
        /// thread to keep job-side enqueueing handle-free. Must run *after*
        /// <see cref="SortNativeAdds"/> so the assigned ids follow deterministic
        /// sort-key order rather than bag-thread arrival order. Pre-reserved
        /// handles (non-Null) are skipped.
        /// </summary>
        internal void ClaimDeferredHandlesForNativeAdds(ref EntityHandleMap entityHandleMap)
        {
            for (int gi = 0; gi < _currentPendingReferences.Length; gi++)
            {
                var refs = _currentPendingReferences[gi];
                if (refs == null || refs.Count == 0)
                {
                    continue;
                }

                var startIndex = _currentNativeAddStartIndices[gi];
                if (startIndex < 0)
                {
                    // No native adds for this group this frame — only managed
                    // adds, which already claimed at enqueue time.
                    continue;
                }

                var count = refs.Count;
                for (int i = startIndex; i < count; i++)
                {
                    if (refs[i] == EntityHandle.Null)
                    {
                        refs[i] = entityHandleMap.ClaimId();
                    }
                }
            }
        }

        internal IterableDictionary<TypeId, IComponentArray>[] currentComponentsToAddPerGroup;

        IterableDictionary<TypeId, IComponentArray>[] lastComponentsToAddPerGroup;

        int[] _currentNumberEntitiesCreatedPerGroup;
        int[] _lastNumberEntitiesCreatedPerGroup;

        int _totalEntitiesCreatedThisFrame;
        int _totalEntitiesCreatedLastFrame;

        List<EntityHandle>[] _currentPendingReferences;
        List<EntityHandle>[] _lastPendingReferences;

        List<ulong>[] _currentNativeAddSortKeys;
        List<ulong>[] _lastNativeAddSortKeys;

        int[] _currentNativeAddStartIndices;
        int[] _lastNativeAddStartIndices;

        // Cached native lists for SortNativeAdds to avoid per-frame allocations
        NativeList<KeyedIndex> _cachedSortBuffer;
        NativeList<int> _cachedSortIndices;
        NativeList<EntityHandle> _cachedSortTempRefs;

        // Scratch for ReorderRangeJob — sized to max(elementSize * count) seen so far.
        // Reused sequentially across per-component reorders within one SortNativeAdds call.
        NativeList<byte> _cachedReorderScratch;

        struct KeyedIndex : IComparable<KeyedIndex>
        {
            public ulong Key;
            public int Index;

            public int CompareTo(KeyedIndex other) => Key.CompareTo(other.Key);
        }

        // Burst-jobified equivalent of NativeList<KeyedIndex>.Sort(). Wrapped in
        // IJob with .Run() so the AOT-compiled sort runs in place of the
        // IL2CPP-generated managed call — same pattern as SortSwapsJob /
        // SortRemovalsJob in SubmissionBurstJobs.cs.
        [BurstCompile]
        struct SortNatAddKeysJob : IJob
        {
            [NativeDisableUnsafePtrRestriction]
            public long Ptr;
            public int Count;

            public void Execute()
            {
                unsafe
                {
                    NativeSortExtension.Sort((KeyedIndex*)Ptr, Count);
                }
            }
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
                _cachedReorderScratch = new NativeList<byte>(64, Allocator.Persistent);
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
                TrecsDebugAssert.That(
                    startIndex >= 0,
                    "Native add start index not set for group {0} despite non-empty sort keys. "
                        + "MarkNativeAddStartIfNeeded must be called before AddPendingNativeAddSortKey.",
                    group
                );

                // Build sortable key+index pairs
                _cachedSortBuffer.Clear();
                for (int i = 0; i < count; i++)
                {
                    _cachedSortBuffer.Add(new KeyedIndex { Key = keys[i], Index = i });
                }

                // Native sort (non-allocating) — Burst-jobified, .Run() blocks
                // until the AOT-compiled sort completes on the main thread.
                unsafe
                {
                    new SortNatAddKeysJob
                    {
                        Ptr = (long)_cachedSortBuffer.GetUnsafePtr(),
                        Count = count,
                    }.Run();
                }

                // Check for duplicates (adjacent after sort)
                for (int i = 1; i < count; i++)
                {
                    TrecsDebugAssert.That(
                        _cachedSortBuffer[i].Key != _cachedSortBuffer[i - 1].Key,
                        "Duplicate native add sort key detected in group {0} (composite key {1}). "
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

                // Apply permutation to all component arrays for this group via a
                // Burst-compiled reorder job. Scratch buffer is sized to the largest
                // (elementSize * count) seen across the per-component loop and reused
                // across .Run() calls — replaces a per-call Allocator.Temp alloc and
                // moves the inner scatter-memcpy loop from IL2CPP-managed into Burst.
                var groupDict = currentComponentsToAddPerGroup[gi];
                var componentArrays = groupDict.UnsafeValues;
                var componentCount = groupDict.Count;

                int maxElementSize = 0;
                for (int ci = 0; ci < componentCount; ci++)
                {
                    var elemSize = componentArrays[ci].ElementSize;
                    if (elemSize > maxElementSize)
                        maxElementSize = elemSize;
                }

                int scratchBytes = (int)((long)maxElementSize * count);
                if (_cachedReorderScratch.Length < scratchBytes)
                    _cachedReorderScratch.Resize(
                        scratchBytes,
                        NativeArrayOptions.UninitializedMemory
                    );

                unsafe
                {
                    var scratchPtr = (long)_cachedReorderScratch.GetUnsafePtr();
                    var permPtr = (long)_cachedSortIndices.GetUnsafePtr();
                    for (int ci = 0; ci < componentCount; ci++)
                    {
                        var arr = componentArrays[ci];
                        new ReorderRangeJob
                        {
                            BufferPtr = (long)arr.GetUnsafePtr(),
                            ScratchPtr = scratchPtr,
                            PermutationPtr = permPtr,
                            ElementSize = arr.ElementSize,
                            StartIndex = startIndex,
                            Count = count,
                        }.Run();
                    }
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
