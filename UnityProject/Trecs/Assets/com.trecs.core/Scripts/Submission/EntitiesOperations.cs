using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;

namespace Trecs.Internal
{
    class EntitiesOperations
    {
        readonly TrecsLog _log;

        Info _lastSubmittedInfo;
        Info _thisSubmissionInfo;

        readonly Func<FastList<MoveInfoEntry>> _newInnerMoveList;
        readonly ActionRef<FastList<MoveInfoEntry>> _recycleInnerMoveList;

        public EntitiesOperations(TrecsLog log, int groupCount)
        {
            _log = log;
            _thisSubmissionInfo.Init(groupCount);
            _lastSubmittedInfo.Init(groupCount);

            _newInnerMoveList = NewInnerMoveList;
            _recycleInnerMoveList = RecycleInnerMoveList;
        }

        public void QueueRemoveGroupOperation(GroupIndex groupId, string caller)
        {
            _thisSubmissionInfo._groupsToRemove.Add((groupId, caller));
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
        /// Queue removal of all entities in a group. More efficient than individual
        /// QueueRemoveOperation calls because it skips per-entity Contains checks
        /// and amortizes the per-group slot lookup.
        /// </summary>
        public void QueueRemoveAllInGroup(GroupIndex group, int entityCount)
        {
            if (entityCount == 0)
                return;

            var removedComponentsPerType = GetOrCreateRemoveList(group);

            for (int i = 0; i < entityCount; i++)
            {
                var entityIndex = new EntityIndex(i, group);

                if (_thisSubmissionInfo._entitiesRemoved.Contains(entityIndex))
                    continue;

                _thisSubmissionInfo._entitiesRemoved.Add(entityIndex);
                _thisSubmissionInfo._removeCount++;
                RevertMoveOperationIfPreviouslyQueued(entityIndex);

                removedComponentsPerType.Add(i);
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
                RemoveEntryByEntityIndex(innerList, fromEntityIndex.Index);
            }
        }

        // Inner list is appended in submission order with no dedup work — that
        // happens in _entitiesMoved at the outer layer. The revert path is rare
        // (only hits when a move and remove are queued for the same entity in
        // the same submission), so a linear scan is fine here.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void RemoveEntryByEntityIndex(FastList<MoveInfoEntry> list, int entityIndex)
        {
            var buf = list._buffer;
            var count = list._count;
            for (int i = 0; i < count; i++)
            {
                if (buf[i].EntityIndex == entityIndex)
                {
                    list.UnorderedRemoveAt(i);
                    return;
                }
            }
            TrecsDebugAssert.That(
                false,
                "RemoveEntryByEntityIndex: entity not found in inner move list"
            );
        }

        public void QueueMoveGroupOperation(
            GroupIndex fromGroupId,
            GroupIndex toGroupId,
            string caller
        )
        {
            _thisSubmissionInfo._groupsToMove.Add((fromGroupId, toGroupId, caller));
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

            GetOrCreateSwapOuter(fromEntityIndex.GroupIndex)
                .RecycleOrAdd(toGroup, _newInnerMoveList, _recycleInnerMoveList)
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
        /// _entitiesRemoved continues to track managed-side removes (queued
        /// via <see cref="QueueRemoveOperation"/> / <see cref="QueueRemoveAllInGroup"/>)
        /// for the cross-source dedup branch below and for mid-tick
        /// <c>IsScheduledForRemove</c> callers.
        /// </para>
        /// </summary>
        public void QueueNativeRemoveOperations(
            FastList<(EntityIndex, int)> sortedRemovals,
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
            FastList<int> cachedList = null;
            int addedCount = 0;

            foreach (var (entityIdx, accessorId) in sortedRemovals)
            {
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
            FastList<(EntityIndex, GroupIndex, int)> sortedSwaps,
            FastList<(EntityIndex, int)> sortedRemovals,
            WorldInfo worldInfo
        )
        {
            // After sort, entries with the same fromGroup are adjacent (EntityIndex
            // sorts by GroupIndex first), and the same (fromGroup, toGroup) pair
            // typically also clusters — partition-flip workloads usually have a
            // single toGroup per fromGroup, hitting both caches ~100%.
            GroupIndex cachedFromGroup = default;
            DenseDictionary<GroupIndex, FastList<MoveInfoEntry>> cachedOuter = null;
            GroupIndex cachedToGroup = default;
            FastList<MoveInfoEntry> cachedInner = null;

            // Hoist dedup-dict empty checks. _entitiesRemoved isn't touched
            // inside this loop, so its size is stable. _entitiesMoved grows
            // here, but entries from sortedSwaps are unique by construction
            // (post-coalesce), so if it starts empty we can skip the TryAdd
            // existence check entirely — every Add succeeds.
            bool checkPriorManagedRemoves = _thisSubmissionInfo._entitiesRemoved.Count > 0;
            bool entityMovedDedupRequired = _thisSubmissionInfo._entitiesMoved.Count > 0;

            var sortedRemovalsBuf = sortedRemovals._buffer;
            int sortedRemovalsCount = sortedRemovals._count;
            int ri = 0;

            foreach (var (fromEntityIndex, toGroup, accessorId) in sortedSwaps)
            {
                // Gate 1: managed-side remove (rare; skip probe when set empty).
                if (
                    checkPriorManagedRemoves
                    && _thisSubmissionInfo._entitiesRemoved.Contains(fromEntityIndex)
                )
                    continue;

                // Gate 2: merge-walk over native-side sortedRemovals.
                // sortedSwaps and sortedRemovals share EntityIndex.CompareTo as
                // primary key, so ri advances monotonically across this foreach.
                while (
                    ri < sortedRemovalsCount
                    && sortedRemovalsBuf[ri].Item1.CompareTo(fromEntityIndex) < 0
                )
                    ri++;
                if (ri < sortedRemovalsCount && sortedRemovalsBuf[ri].Item1.Equals(fromEntityIndex))
                    continue;

                if (entityMovedDedupRequired)
                {
                    // Skip if already queued for a move (first move wins,
                    // dedup with managed moves).
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

                if (fromEntityIndex.GroupIndex != cachedFromGroup || cachedOuter == null)
                {
                    cachedFromGroup = fromEntityIndex.GroupIndex;
                    cachedOuter = GetOrCreateSwapOuter(cachedFromGroup);
                    cachedToGroup = default;
                    cachedInner = null;
                }

                if (toGroup != cachedToGroup || cachedInner == null)
                {
                    cachedToGroup = toGroup;
                    cachedInner = cachedOuter.RecycleOrAdd(
                        toGroup,
                        _newInnerMoveList,
                        _recycleInnerMoveList
                    );
                }

                cachedInner.Add(new MoveInfoEntry { EntityIndex = fromEntityIndex.Index });
            }
        }

        /// <summary>
        /// After a move operation's swap-back changes entity positions in a group,
        /// update any pending remove indices for that group so they target the correct positions.
        /// </summary>
        internal void UpdateRemoveIndicesAfterMoveSwapBack(
            GroupIndex fromGroup,
            DenseDictionary<int, int> swapBackMapping
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

        public void ExecuteRemoveAndSwappingOperations(
            Action<
                DenseDictionary<GroupIndex, FastList<MoveInfoEntry>>[],
                DenseDictionary<EntityIndex, (EntityIndex, GroupIndex)>,
                EntitySubmitter
            > moveEntities,
            Action<FastList<int>[], EntitySubmitter> removeEntities,
            Action<GroupIndex, EntitySubmitter> removeGroup,
            Action<GroupIndex, GroupIndex, EntitySubmitter> swapGroup,
            EntitySubmitter ecsRoot
        )
        {
            // Swap before executing, so observer callbacks that fire during
            // the upcoming remove/move callbacks queue *new* ops into the
            // freshly-empty _thisSubmissionInfo. The outer SubmitImpl
            // loop then picks those up in a subsequent iteration. This is
            // what keeps the cascade iterative rather than recursive — see
            // the cascade contract on EntitySubmitter.SubmitImpl.
            (_thisSubmissionInfo, _lastSubmittedInfo) = (_lastSubmittedInfo, _thisSubmissionInfo);

            foreach (var (group, caller) in _lastSubmittedInfo._groupsToRemove)
                try
                {
                    removeGroup(group, ecsRoot);
                }
                catch
                {
                    var str = $"Crash while removing a whole group on {group} from : {caller}";

                    _log.Error(str);

                    throw;
                }

            foreach (var (fromGroup, toGroup, caller) in _lastSubmittedInfo._groupsToMove)
                try
                {
                    swapGroup(fromGroup, toGroup, ecsRoot);
                }
                catch
                {
                    var str =
                        $"Crash while swapping a whole group on {fromGroup} {toGroup} from : {caller}";

                    _log.Error(str);

                    throw;
                }

            // _entitiesMoved / _entitiesRemoved are populated in lock-step with the
            // per-group arrays, so these counts are a correct proxy for "any entries
            // in the array" — no scan needed.
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

            using (TrecsProfiling.Start("Clear Submitted Info"))
            {
                _lastSubmittedInfo.Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        FastList<int> GetOrCreateRemoveList(GroupIndex group)
        {
            ref var slot = ref _thisSubmissionInfo._currentRemoveEntitiesOperations[group.Index];
            slot ??= new FastList<int>();
            return slot;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        DenseDictionary<GroupIndex, FastList<MoveInfoEntry>> GetOrCreateSwapOuter(
            GroupIndex fromGroup
        )
        {
            ref var slot = ref _thisSubmissionInfo._currentSwapEntitiesOperations[fromGroup.Index];
            slot ??= new DenseDictionary<GroupIndex, FastList<MoveInfoEntry>>();
            return slot;
        }

        static FastList<MoveInfoEntry> NewInnerMoveList()
        {
            // Pre-size to 1 so the first Add doesn't trigger AllocateMore
            // off the cached Array.Empty<T>() backing — matches the per-frame
            // zero-alloc invariant the coalescer asserts on after one warmup.
            return new FastList<MoveInfoEntry>(1);
        }

        static void RecycleInnerMoveList(ref FastList<MoveInfoEntry> target)
        {
            // FastList.Clear is the moral equivalent of DenseDictionary.Recycle
            // here: reset count to 0 but keep the underlying buffer for the
            // next submission. MoveInfoEntry is unmanaged so Clear is O(1).
            target.Clear();
        }

        struct Info
        {
            // Per-from-group outer; each active slot holds a dict keyed by to-group.
            // Null for groups with no moves this frame. Recycled in place at
            // end-of-frame (buffers retained for next frame's reuse).
            internal DenseDictionary<
                GroupIndex,
                FastList<MoveInfoEntry>
            >[] _currentSwapEntitiesOperations;

            // Per-group remove lists. Null for groups with no removes this frame.
            // Cleared in place at end-of-frame.
            internal FastList<int>[] _currentRemoveEntitiesOperations;

            internal DenseDictionary<
                EntityIndex,
                (EntityIndex fromEntityIndex, GroupIndex toGroup)
            > _entitiesMoved;
            internal DenseHashSet<EntityIndex> _entitiesRemoved;

            // Total unique remove ops queued this submission (managed + native).
            // Used in lieu of _entitiesRemoved.Count, because the native bulk-remove
            // path (QueueNativeRemoveOperations) no longer populates _entitiesRemoved
            // — the NatSwap Queue gate uses a merge-walk over sortedRemovals
            // instead. _entitiesRemoved still tracks managed-side removes only
            // (rare in bulk workloads), so it's no longer a reliable count for
            // "are there any removes queued this submission?".
            internal int _removeCount;
            public FastList<(GroupIndex, GroupIndex, string)> _groupsToMove;
            public FastList<(GroupIndex, string)> _groupsToRemove;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool AnyOperationQueued()
            {
                // Entity-set counts are a proxy for array contents: every queued
                // op adds to the relevant set before touching the array.
                return _entitiesMoved.Count > 0
                    || _removeCount > 0
                    || _groupsToMove.Count > 0
                    || _groupsToRemove.Count > 0;
            }

            internal void Clear()
            {
                for (int i = 0; i < _currentSwapEntitiesOperations.Length; i++)
                {
                    var fromGroup = _currentSwapEntitiesOperations[i];
                    if (fromGroup == null)
                        continue;
                    // Recycle inner lists so their buffers can be reused next frame.
                    foreach (var entry in fromGroup)
                        entry.Value.Clear();
                    fromGroup.Recycle();
                }

                for (int i = 0; i < _currentRemoveEntitiesOperations.Length; i++)
                {
                    _currentRemoveEntitiesOperations[i]?.Clear();
                }

                _entitiesMoved.Recycle();
                _entitiesRemoved.Recycle();
                _removeCount = 0;
                _groupsToRemove.Clear();
                _groupsToMove.Clear();
            }

            internal void Init(int groupCount)
            {
                _entitiesMoved =
                    new DenseDictionary<
                        EntityIndex,
                        (EntityIndex fromEntityIndex, GroupIndex toGroup)
                    >();
                _entitiesRemoved = new DenseHashSet<EntityIndex>();
                _groupsToRemove = new FastList<(GroupIndex, string)>();
                _groupsToMove = new FastList<(GroupIndex, GroupIndex, string)>();

                _currentSwapEntitiesOperations = new DenseDictionary<
                    GroupIndex,
                    FastList<MoveInfoEntry>
                >[groupCount];
                _currentRemoveEntitiesOperations = new FastList<int>[groupCount];
            }
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct MoveInfo
    {
        public int ToIndex;

        /// <summary>
        /// Precomputed resolved source index after accounting for prior swap-backs.
        /// Set during move precomputation in MoveEntities, before the component-type loop.
        /// </summary>
        public int ResolvedFromIndex;
    }

    // Inner batch entry for per-(fromGroup, toGroup) move queues. Replaces a
    // DenseDictionary<int, MoveInfo>: dedup is already guaranteed by the outer
    // _entitiesMoved set, so the inner container only needs append + indexed
    // iteration, which a flat list handles in O(1) without any hashing.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct MoveInfoEntry
    {
        public int EntityIndex;
        public MoveInfo Info;
    }
}
