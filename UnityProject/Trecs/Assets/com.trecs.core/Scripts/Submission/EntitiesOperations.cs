using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;

namespace Trecs.Internal
{
    class EntitiesOperations
    {
        readonly TrecsLog _log;

        Info _lastSubmittedInfo;
        Info _thisSubmissionInfo;

        readonly Func<DenseDictionary<int, MoveInfo>> _newInnerMoveDict;
        readonly ActionRef<DenseDictionary<int, MoveInfo>> _recycleInnerMoveDict;

        public EntitiesOperations(TrecsLog log, int groupCount)
        {
            _log = log;
            _thisSubmissionInfo.Init(groupCount);
            _lastSubmittedInfo.Init(groupCount);

            _newInnerMoveDict = NewInnerMoveDict;
            _recycleInnerMoveDict = RecycleInnerMoveDict;
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
                // dict's entry for toGroup must also exist since the same queue
                // path created both.
                var swappedComponentsPerType = _thisSubmissionInfo._currentSwapEntitiesOperations[
                    fromEntityIndex.GroupIndex.Index
                ];

                swappedComponentsPerType[val.toGroup].RemoveMustExist(fromEntityIndex.Index);
            }
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
                .RecycleOrAdd(toGroup, _newInnerMoveDict, _recycleInnerMoveDict)
                .Add(fromEntityIndex.Index, default);
        }

        /// <summary>
        /// Batch queue remove operations from pre-sorted native data.
        /// </summary>
        public void QueueNativeRemoveOperations(
            NativeList<(EntityIndex, int)> sortedRemovals,
            WorldInfo worldInfo
        )
        {
            foreach (var (entityIdx, accessorId) in sortedRemovals)
            {
                if (_thisSubmissionInfo._entitiesRemoved.Contains(entityIdx))
                    continue;

                _thisSubmissionInfo._entitiesRemoved.Add(entityIdx);
                RevertMoveOperationIfPreviouslyQueued(entityIdx);

                GetOrCreateRemoveList(entityIdx.GroupIndex).Add(entityIdx.Index);
            }
        }

        /// <summary>
        /// Batch queue move operations from pre-sorted native data.
        /// </summary>
        public void QueueNativeMoveOperations(
            NativeList<(EntityIndex, GroupIndex, int)> sortedSwaps,
            WorldInfo worldInfo
        )
        {
            foreach (var (fromEntityIndex, toGroup, accessorId) in sortedSwaps)
            {
                // Remove supersedes swap
                if (_thisSubmissionInfo._entitiesRemoved.Contains(fromEntityIndex))
                    continue;

                // Skip if already queued for a move (first move wins, dedup with managed moves)
                if (
                    !_thisSubmissionInfo._entitiesMoved.TryAdd(
                        fromEntityIndex,
                        (fromEntityIndex, toGroup),
                        out _
                    )
                )
                    continue;

                GetOrCreateSwapOuter(fromEntityIndex.GroupIndex)
                    .RecycleOrAdd(toGroup, _newInnerMoveDict, _recycleInnerMoveDict)
                    .Add(fromEntityIndex.Index, default);
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
                    TrecsAssert.That(++hops <= maxHops, "Cycle detected in swap-back mapping");
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
                DenseDictionary<GroupIndex, DenseDictionary<int, MoveInfo>>[],
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
            // freshly-empty _thisSubmissionInfo. The outer SubmitEntitiesImpl
            // loop then picks those up in a subsequent iteration. This is
            // what keeps the cascade iterative rather than recursive — see
            // the cascade contract on EntitySubmitter.SubmitEntitiesImpl.
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

            if (_lastSubmittedInfo._entitiesRemoved.Count > 0)
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
        DenseDictionary<GroupIndex, DenseDictionary<int, MoveInfo>> GetOrCreateSwapOuter(
            GroupIndex fromGroup
        )
        {
            ref var slot = ref _thisSubmissionInfo._currentSwapEntitiesOperations[fromGroup.Index];
            slot ??= new DenseDictionary<GroupIndex, DenseDictionary<int, MoveInfo>>();
            return slot;
        }

        static DenseDictionary<int, MoveInfo> NewInnerMoveDict()
        {
            return new DenseDictionary<int, MoveInfo>();
        }

        static void RecycleInnerMoveDict(ref DenseDictionary<int, MoveInfo> target)
        {
            target.Recycle();
        }

        struct Info
        {
            // Per-from-group outer; each active slot holds a dict keyed by to-group.
            // Null for groups with no moves this frame. Recycled in place at
            // end-of-frame (buffers retained for next frame's reuse).
            internal DenseDictionary<
                GroupIndex,
                DenseDictionary<int, MoveInfo>
            >[] _currentSwapEntitiesOperations;

            // Per-group remove lists. Null for groups with no removes this frame.
            // Cleared in place at end-of-frame.
            internal FastList<int>[] _currentRemoveEntitiesOperations;

            internal DenseDictionary<
                EntityIndex,
                (EntityIndex fromEntityIndex, GroupIndex toGroup)
            > _entitiesMoved;
            internal DenseHashSet<EntityIndex> _entitiesRemoved;
            public FastList<(GroupIndex, GroupIndex, string)> _groupsToMove;
            public FastList<(GroupIndex, string)> _groupsToRemove;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool AnyOperationQueued()
            {
                // Entity-set counts are a proxy for array contents: every queued
                // op adds to the relevant set before touching the array.
                return _entitiesMoved.Count > 0
                    || _entitiesRemoved.Count > 0
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
                    // Recycle inner dicts so their buffers can be reused next frame.
                    foreach (var entry in fromGroup)
                        entry.Value.Recycle();
                    fromGroup.Recycle();
                }

                for (int i = 0; i < _currentRemoveEntitiesOperations.Length; i++)
                {
                    _currentRemoveEntitiesOperations[i]?.Clear();
                }

                _entitiesMoved.Recycle();
                _entitiesRemoved.Recycle();
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
                    DenseDictionary<int, MoveInfo>
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
}
