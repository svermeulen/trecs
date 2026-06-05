using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    class EntitiesOperations : IDisposable
    {
        Info _lastSubmittedInfo;
        Info _thisSubmissionInfo;

        public EntitiesOperations(int groupCount)
        {
            _thisSubmissionInfo.Init(groupCount);
            _lastSubmittedInfo.Init(groupCount);
        }

        public void Dispose()
        {
            DisposeInnerMoveLists(ref _thisSubmissionInfo);
            DisposeInnerMoveLists(ref _lastSubmittedInfo);
        }

        static void DisposeInnerMoveLists(ref Info info)
        {
            if (info._currentSwapEntitiesOperations == null)
                return;
            for (int i = 0; i < info._currentSwapEntitiesOperations.Length; i++)
            {
                var outerDict = info._currentSwapEntitiesOperations[i];
                if (outerDict == null)
                    continue;
                foreach (var entry in outerDict)
                {
                    if (entry.Value.IsCreated)
                        entry.Value.Dispose();
                }
            }
            for (int i = 0; i < info._moveListPool.Count; i++)
            {
                if (info._moveListPool[i].IsCreated)
                    info._moveListPool[i].Dispose();
            }
        }

        public bool IsScheduledForRemove(EntityIndex entityIndex)
        {
            return _thisSubmissionInfo._entitiesRemoved.Contains(entityIndex);
        }

        public bool IsScheduledForMove(EntityIndex entityIndex)
        {
            return _thisSubmissionInfo._entitiesMoved.ContainsKey(entityIndex);
        }

        public void QueueRemoveOperation(
            EntityIndex fromEntityIndex,
            IComponentBuilder[] componentBuilders
        )
        {
            if (_thisSubmissionInfo._entitiesRemoved.Contains(fromEntityIndex))
                return;

            _thisSubmissionInfo._entitiesRemoved.Add(fromEntityIndex);
            _thisSubmissionInfo._removeCount++;
            RevertMoveOperationIfPreviouslyQueued(fromEntityIndex);

            GetOrCreateRemoveList(fromEntityIndex.GroupIndex).Add(fromEntityIndex.Index);
        }

        /// <summary>
        /// Mark a whole group for removal of its entire (post-add) contents. O(1)
        /// and idempotent: it sets a per-group flag rather than enumerating the
        /// group's current entities, so the live count is read at execute time —
        /// after this submission's moves, per-entity removes, and adds have been
        /// applied. The flag is consumed by the full-group-removal phase
        /// (<see cref="ExecuteFullGroupRemovals"/>), which runs <em>after</em>
        /// adds so an entity added into a bulk-removed group in the same submit
        /// fires both OnAdded and OnRemoved. Any per-entity removes queued for the
        /// same group still run in the normal remove phase; the full-group phase
        /// then clears whatever survivors remain.
        /// </summary>
        public void QueueRemoveAllInGroup(GroupIndex group)
        {
            ref var slot = ref _thisSubmissionInfo._fullGroupRemoval[group.Index];
            if (!slot)
            {
                slot = true;
                _thisSubmissionInfo._fullGroupRemovalCount++;
            }
        }

        /// <summary>
        /// If a move was previously queued for this entity, remove it from the swap operations.
        /// Remove supersedes swap.
        /// </summary>
        void RevertMoveOperationIfPreviouslyQueued(EntityIndex fromEntityIndex)
        {
            if (
                _thisSubmissionInfo._entitiesMoved.TryRemove(
                    fromEntityIndex,
                    out (EntityIndex fromEntityIndex, GroupIndex toGroup) val
                )
            )
            {
                // The outer slot must exist since the move was queued — its inner
                // list's entry for toGroup must also exist since the same queue
                // path created both.
                var swappedComponentsPerType = _thisSubmissionInfo._currentSwapEntitiesOperations[
                    fromEntityIndex.GroupIndex.Index
                ];

                var innerList = swappedComponentsPerType[val.toGroup];
                RemoveEntryByEntityIndex(ref innerList, fromEntityIndex.Index);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void RemoveEntryByEntityIndex(ref NativeList<MoveInfoEntry> list, int entityIndex)
        {
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i].EntityIndex == entityIndex)
                {
                    list.RemoveAtSwapBack(i);
                    return;
                }
            }
            TrecsDebugAssert.That(
                false,
                "RemoveEntryByEntityIndex: entity not found in inner move list"
            );
        }

        public void QueueMoveOperation(
            EntityIndex fromEntityIndex,
            GroupIndex toGroup,
            IComponentBuilder[] componentBuilders
        )
        {
            // Remove supersedes move (consistent with QueueNativeMoveOperations)
            if (_thisSubmissionInfo._entitiesRemoved.Contains(fromEntityIndex))
                return;

            _thisSubmissionInfo._entitiesMoved.Add(fromEntityIndex, (fromEntityIndex, toGroup));

            GetOrCreateInnerMoveList(fromEntityIndex.GroupIndex, toGroup)
                .Add(new MoveInfoEntry { EntityIndex = fromEntityIndex.Index });
        }

        /// <summary>
        /// Batch queue remove operations from pre-sorted native data.
        /// <para>
        /// Native-vs-native duplicate dedup is via adjacent-compare (cheap,
        /// since sortedRemovals is sorted by EntityIndex). The hashset
        /// <c>_entitiesRemoved</c> is intentionally <em>not</em> populated
        /// here: its only in-pipeline consumer was the NatSwap Queue gate at
        /// <see cref="QueueNativeMoveOperations"/>, which now uses a
        /// merge-walk over <paramref name="sortedRemovals"/> instead.
        /// _entitiesRemoved continues to track managed-side per-entity removes
        /// (queued via <see cref="QueueRemoveOperation"/>) for the cross-source
        /// dedup branch below and for mid-tick <c>IsScheduledForRemove</c>
        /// callers. Whole-group removals (<see cref="QueueRemoveAllInGroup"/>)
        /// are tracked separately via the per-group full-removal markers and do
        /// not touch this set.
        /// </para>
        /// </summary>
        public void QueueNativeRemoveOperations(
            NativeList<RemovalEntry> sortedRemovals,
            WorldInfo worldInfo
        )
        {
            // Cross-source dedup against managed removes only fires when something
            // landed in _entitiesRemoved earlier this tick — typically 0 in
            // bulk-native workloads, so the per-entity Contains probe is skipped.
            bool checkPriorManagedRemoves = _thisSubmissionInfo._entitiesRemoved.Count > 0;
            // Same idea: RevertMove's TryRemove on _entitiesMoved is a hashset
            // probe per entity. Skip the call entirely when the move map is empty.
            bool hasPriorMoves = _thisSubmissionInfo._entitiesMoved.Count > 0;

            // sortedRemovals is sorted by EntityIndex (group-major), so same-group
            // runs cluster and adjacent-compare dedup handles native-vs-native
            // duplicates without touching a hashset.
            EntityIndex prev = default;
            bool hasPrev = false;
            GroupIndex cachedGroup = default;
            List<int> cachedList = null;
            int addedCount = 0;

            unsafe
            {
                var removalsPtr = (RemovalEntry*)sortedRemovals.GetUnsafeReadOnlyPtr();
                for (int ri = 0; ri < sortedRemovals.Length; ri++)
                {
                    var entityIdx = removalsPtr[ri].EntityIndex;

                    if (hasPrev && entityIdx == prev)
                        continue;
                    prev = entityIdx;
                    hasPrev = true;

                    if (
                        checkPriorManagedRemoves
                        && _thisSubmissionInfo._entitiesRemoved.Contains(entityIdx)
                    )
                        continue;

                    if (hasPriorMoves)
                        RevertMoveOperationIfPreviouslyQueued(entityIdx);

                    if (entityIdx.GroupIndex != cachedGroup || cachedList == null)
                    {
                        cachedGroup = entityIdx.GroupIndex;
                        cachedList = GetOrCreateRemoveList(cachedGroup);
                    }
                    cachedList.Add(entityIdx.Index);
                    addedCount++;
                }
            }

            _thisSubmissionInfo._removeCount += addedCount;
        }

        /// <summary>
        /// Batch queue move operations from pre-sorted native data.
        /// <para>
        /// The "remove supersedes swap" gate has two parts: a hashset probe
        /// against <c>_entitiesRemoved</c> for managed-side removes (typically
        /// tiny — populated only by managed <c>QueueRemoveOperation</c> calls
        /// earlier this tick), and a merge-walk against
        /// <paramref name="sortedRemovals"/> for native-side removes (the bulk
        /// case). Both lists share the same <see cref="EntityIndex.CompareTo"/>
        /// primary sort key, so the merge-walk is one amortized pointer advance
        /// per swap with no hashing.
        /// </para>
        /// </summary>
        public void QueueNativeMoveOperations(
            NativeList<SwapEntry> sortedSwaps,
            NativeList<RemovalEntry> sortedRemovals,
            WorldInfo worldInfo
        )
        {
            GroupIndex cachedFromGroup = default;
            GroupIndex cachedToGroup = default;
            NativeList<MoveInfoEntry> cachedInner = default;
            bool hasCachedInner = false;

            bool checkPriorManagedRemoves = _thisSubmissionInfo._entitiesRemoved.Count > 0;
            bool entityMovedDedupRequired = _thisSubmissionInfo._entitiesMoved.Count > 0;

            int sortedRemovalsCount = sortedRemovals.Length;
            int ri = 0;

            unsafe
            {
                var swapsPtr = (SwapEntry*)sortedSwaps.GetUnsafeReadOnlyPtr();
                var removalsPtr =
                    sortedRemovalsCount > 0
                        ? (RemovalEntry*)sortedRemovals.GetUnsafeReadOnlyPtr()
                        : null;

                for (int si = 0; si < sortedSwaps.Length; si++)
                {
                    var swap = swapsPtr[si];
                    var fromEntityIndex = swap.EntityIndex;
                    var toGroup = swap.ToGroup;

                    if (
                        checkPriorManagedRemoves
                        && _thisSubmissionInfo._entitiesRemoved.Contains(fromEntityIndex)
                    )
                        continue;

                    while (
                        ri < sortedRemovalsCount
                        && removalsPtr[ri].EntityIndex.CompareTo(fromEntityIndex) < 0
                    )
                        ri++;
                    if (
                        ri < sortedRemovalsCount
                        && removalsPtr[ri].EntityIndex.Equals(fromEntityIndex)
                    )
                        continue;

                    if (entityMovedDedupRequired)
                    {
                        if (
                            !_thisSubmissionInfo._entitiesMoved.TryAdd(
                                fromEntityIndex,
                                (fromEntityIndex, toGroup),
                                out _
                            )
                        )
                            continue;
                    }
                    else
                    {
                        _thisSubmissionInfo._entitiesMoved.Add(
                            fromEntityIndex,
                            (fromEntityIndex, toGroup)
                        );
                    }

                    if (fromEntityIndex.GroupIndex != cachedFromGroup)
                    {
                        cachedFromGroup = fromEntityIndex.GroupIndex;
                        cachedToGroup = default;
                        hasCachedInner = false;
                    }

                    if (toGroup != cachedToGroup || !hasCachedInner)
                    {
                        cachedToGroup = toGroup;
                        cachedInner = GetOrCreateInnerMoveList(cachedFromGroup, toGroup);
                        hasCachedInner = true;
                    }

                    cachedInner.Add(new MoveInfoEntry { EntityIndex = fromEntityIndex.Index });
                }
            }
        }

        /// <summary>
        /// After a move operation's swap-back changes entity positions in a group,
        /// update any pending remove indices for that group so they target the correct positions.
        /// </summary>
        internal void UpdateRemoveIndicesAfterMoveSwapBack(
            GroupIndex fromGroup,
            IterableDictionary<int, int> swapBackMapping
        )
        {
            var removeList = _lastSubmittedInfo._currentRemoveEntitiesOperations[fromGroup.Index];
            if (removeList == null || removeList.Count == 0)
                return;

            for (int i = 0; i < removeList.Count; i++)
            {
                var idx = removeList[i];
#if TRECS_INTERNAL_CHECKS && DEBUG
                var maxHops = swapBackMapping.Count;
                var hops = 0;
#endif
                while (swapBackMapping.TryGetValue(idx, out var newIndex))
                {
                    idx = newIndex;
#if TRECS_INTERNAL_CHECKS && DEBUG
                    TrecsDebugAssert.That(++hops <= maxHops, "Cycle detected in swap-back mapping");
#endif
                }
                removeList[i] = idx;
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool AnyOperationQueued()
        {
            return _thisSubmissionInfo.AnyOperationQueued();
        }

        // Swaps the double-buffered Info and applies this submission's moves +
        // per-entity removes. Deliberately does NOT clear _lastSubmittedInfo:
        // the full-group-removal markers it now also holds must survive past
        // AddEntities so they execute after adds (see ExecuteFullGroupRemovals).
        // The caller (EntitySubmitter.SingleSubmission) owns the try/finally that
        // guarantees ClearLastSubmittedInfo runs once all three phases —
        // moves+removes, adds, full-group removals — have been attempted.
        public void ExecuteRemoveAndSwappingOperations(
            Action<
                IterableDictionary<GroupIndex, NativeList<MoveInfoEntry>>[],
                IterableDictionary<EntityIndex, (EntityIndex, GroupIndex)>,
                EntitySubmitter
            > moveEntities,
            Action<List<int>[], EntitySubmitter> removeEntities,
            EntitySubmitter ecsRoot
        )
        {
            (_thisSubmissionInfo, _lastSubmittedInfo) = (_lastSubmittedInfo, _thisSubmissionInfo);

            if (_lastSubmittedInfo._entitiesMoved.Count > 0)
            {
                moveEntities(
                    _lastSubmittedInfo._currentSwapEntitiesOperations,
                    _lastSubmittedInfo._entitiesMoved,
                    ecsRoot
                );
            }

            if (_lastSubmittedInfo._removeCount > 0)
            {
                removeEntities(_lastSubmittedInfo._currentRemoveEntitiesOperations, ecsRoot);
            }
        }

        // Final phase of a SingleSubmission: execute any whole-group removals
        // queued via QueueRemoveAllInGroup. Runs after AddEntities so entities
        // added into a bulk-removed group this submit still fire OnAdded first.
        // Callbacks fired here queue into the (already swapped) _thisSubmissionInfo
        // and so are processed in the next cascade iteration.
        public void ExecuteFullGroupRemovals(
            Action<bool[], EntitySubmitter> fullGroupRemoveEntities,
            EntitySubmitter ecsRoot
        )
        {
            if (_lastSubmittedInfo._fullGroupRemovalCount > 0)
            {
                fullGroupRemoveEntities(_lastSubmittedInfo._fullGroupRemoval, ecsRoot);
            }
        }

        public void ClearLastSubmittedInfo()
        {
            using (TrecsProfiling.Start("Clear Submitted Info"))
            {
                _lastSubmittedInfo.Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        List<int> GetOrCreateRemoveList(GroupIndex group)
        {
            ref var slot = ref _thisSubmissionInfo._currentRemoveEntitiesOperations[group.Index];
            slot ??= new List<int>();
            return slot;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        NativeList<MoveInfoEntry> GetOrCreateInnerMoveList(GroupIndex fromGroup, GroupIndex toGroup)
        {
            ref var outerSlot = ref _thisSubmissionInfo._currentSwapEntitiesOperations[
                fromGroup.Index
            ];
            outerSlot ??= new IterableDictionary<GroupIndex, NativeList<MoveInfoEntry>>();

            ref var innerList = ref outerSlot.GetOrAdd(toGroup, out _);

            if (!innerList.IsCreated)
            {
                var pool = _thisSubmissionInfo._moveListPool;
                if (pool.Count > 0)
                {
                    innerList = pool[pool.Count - 1];
                    pool.RemoveAt(pool.Count - 1);
                }
                else
                {
                    innerList = new NativeList<MoveInfoEntry>(1, Allocator.Persistent);
                }
            }

            return innerList;
        }

        struct Info
        {
            internal IterableDictionary<
                GroupIndex,
                NativeList<MoveInfoEntry>
            >[] _currentSwapEntitiesOperations;

            internal List<NativeList<MoveInfoEntry>> _moveListPool;

            internal List<int>[] _currentRemoveEntitiesOperations;

            internal IterableDictionary<
                EntityIndex,
                (EntityIndex fromEntityIndex, GroupIndex toGroup)
            > _entitiesMoved;
            internal IterableHashSet<EntityIndex> _entitiesRemoved;

            internal int _removeCount;

            // Per-group "remove this group's entire contents" markers, indexed by
            // GroupIndex.Index. Set O(1) by QueueRemoveAllInGroup; the live count
            // is read at execute time. _fullGroupRemovalCount mirrors the number
            // of set flags so AnyOperationQueued / Clear can early-out cheaply.
            internal bool[] _fullGroupRemoval;
            internal int _fullGroupRemovalCount;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool AnyOperationQueued()
            {
                return _entitiesMoved.Count > 0 || _removeCount > 0 || _fullGroupRemovalCount > 0;
            }

            internal void Clear()
            {
                for (int i = 0; i < _currentSwapEntitiesOperations.Length; i++)
                {
                    var fromGroup = _currentSwapEntitiesOperations[i];
                    if (fromGroup == null)
                        continue;
                    foreach (var entry in fromGroup)
                    {
                        entry.Value.Clear();
                        _moveListPool.Add(entry.Value);
                    }
                    fromGroup.Recycle();
                }

                for (int i = 0; i < _currentRemoveEntitiesOperations.Length; i++)
                {
                    _currentRemoveEntitiesOperations[i]?.Clear();
                }

                if (_fullGroupRemovalCount > 0)
                {
                    Array.Clear(_fullGroupRemoval, 0, _fullGroupRemoval.Length);
                    _fullGroupRemovalCount = 0;
                }

                _entitiesMoved.Recycle();
                _entitiesRemoved.Recycle();
                _removeCount = 0;
            }

            internal void Init(int groupCount)
            {
                _entitiesMoved =
                    new IterableDictionary<
                        EntityIndex,
                        (EntityIndex fromEntityIndex, GroupIndex toGroup)
                    >();
                _entitiesRemoved = new IterableHashSet<EntityIndex>();

                _currentSwapEntitiesOperations = new IterableDictionary<
                    GroupIndex,
                    NativeList<MoveInfoEntry>
                >[groupCount];
                _moveListPool = new List<NativeList<MoveInfoEntry>>();
                _currentRemoveEntitiesOperations = new List<int>[groupCount];
                _fullGroupRemoval = new bool[groupCount];
                _fullGroupRemovalCount = 0;
            }
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal struct MoveInfo
    {
        public int ToIndex;

        /// <summary>
        /// Precomputed resolved source index after accounting for prior swap-backs.
        /// Set during move precomputation in MoveEntities, before the component-type loop.
        /// </summary>
        public int ResolvedFromIndex;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal struct MoveInfoEntry
    {
        public int EntityIndex;
        public MoveInfo Info;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal struct RemovalEntry
    {
        public EntityIndex EntityIndex;
        public int AccessorId;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal struct SwapEntry
    {
        public EntityIndex EntityIndex;
        public GroupIndex ToGroup;
        public int AccessorId;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal struct NativeTagOp
    {
        public int AccessorId;
        public EntityIndex EntityIndex;
        public int TagId;
        public bool IsSet;
    }
}
