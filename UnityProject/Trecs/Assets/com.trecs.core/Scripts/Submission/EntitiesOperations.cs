using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;

namespace Trecs.Internal
{
    class EntitiesOperations
    {
        static readonly TrecsLog _log = new(nameof(EntitiesOperations));

        Info _lastSubmittedInfo;
        Info _thisSubmissionInfo;

        readonly Func<DenseDictionary<Group, DenseDictionary<int, MoveInfo>>> _newGroupDictionary;
        readonly Func<FastList<int>> _newList;
        readonly ActionRef<FastList<int>> _clearList;

        readonly ActionRef<
            DenseDictionary<Group, DenseDictionary<int, MoveInfo>>
        > _recycleDicitionaryWithCaller;
        readonly Func<DenseDictionary<int, MoveInfo>> _newListWithCaller;
        readonly ActionRef<DenseDictionary<int, MoveInfo>> _clearListWithCaller;

        public EntitiesOperations()
        {
            _thisSubmissionInfo.Init();
            _lastSubmittedInfo.Init();

            _newGroupDictionary = NewGroupDictionary;
            _newList = NewList;
            _clearList = ClearList;
            _recycleDicitionaryWithCaller = RecycleDicitionaryWithCaller;
            _newListWithCaller = NewListWithCaller;
            _clearListWithCaller = ClearListWithCaller;
        }

        public void QueueRemoveGroupOperation(Group groupId, string caller)
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

            var removedComponentsPerType =
                _thisSubmissionInfo._currentRemoveEntitiesOperations.RecycleOrAdd(
                    fromEntityIndex.Group,
                    _newList,
                    _clearList
                );

            removedComponentsPerType.Add(fromEntityIndex.Index);
        }

        /// <summary>
        /// Queue removal of all entities in a group. More efficient than individual
        /// QueueRemoveOperation calls because it skips per-entity Contains checks
        /// and amortizes dictionary lookups.
        /// </summary>
        public void QueueRemoveAllInGroup(Group group, int entityCount)
        {
            if (entityCount == 0)
                return;

            var removedComponentsPerType =
                _thisSubmissionInfo._currentRemoveEntitiesOperations.RecycleOrAdd(
                    group,
                    _newList,
                    _clearList
                );

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
                    out (EntityIndex fromEntityIndex, Group toGroup) val
                )
            )
            {
                var swappedComponentsPerType = _thisSubmissionInfo._currentSwapEntitiesOperations[
                    fromEntityIndex.Group
                ];

                swappedComponentsPerType[val.toGroup].RemoveMustExist(fromEntityIndex.Index);
            }
        }

        public void QueueMoveGroupOperation(Group fromGroupId, Group toGroupId, string caller)
        {
            _thisSubmissionInfo._groupsToMove.Add((fromGroupId, toGroupId, caller));
        }

        public void QueueMoveOperation(
            EntityIndex fromEntityIndex,
            Group toGroup,
            IComponentBuilder[] componentBuilders
        )
        {
            // Remove supersedes move (consistent with QueueNativeMoveOperations)
            if (_thisSubmissionInfo._entitiesRemoved.Contains(fromEntityIndex))
                return;

            _thisSubmissionInfo._entitiesMoved.Add(fromEntityIndex, (fromEntityIndex, toGroup));

            //Get (or create) the dictionary that holds the entities that are swapping from fromEntityIndex group
            var swappedComponentsPerType =
                _thisSubmissionInfo._currentSwapEntitiesOperations.RecycleOrAdd(
                    fromEntityIndex.Group,
                    _newGroupDictionary,
                    _recycleDicitionaryWithCaller
                );

            swappedComponentsPerType
                .RecycleOrAdd(toGroup, _newListWithCaller, _clearListWithCaller)
                .Add(fromEntityIndex.Index, default);
        }

        /// <summary>
        /// Batch queue remove operations from pre-sorted native data.
        /// Groups entities by their group to amortize dictionary lookups.
        /// </summary>
        public void QueueNativeRemoveOperations(
            NativeList<(EntityIndex, int)> sortedRemovals,
            WorldInfo worldInfo
        )
        {
            Group cachedGroup = default;
            FastList<int> cachedRemoveList = null;

            foreach (var (entityIdx, accessorId) in sortedRemovals)
            {
                if (_thisSubmissionInfo._entitiesRemoved.Contains(entityIdx))
                    continue;

                _thisSubmissionInfo._entitiesRemoved.Add(entityIdx);
                RevertMoveOperationIfPreviouslyQueued(entityIdx);

                if (entityIdx.Group != cachedGroup)
                {
                    cachedGroup = entityIdx.Group;
                    cachedRemoveList =
                        _thisSubmissionInfo._currentRemoveEntitiesOperations.RecycleOrAdd(
                            cachedGroup,
                            _newList,
                            _clearList
                        );
                }

                cachedRemoveList.Add(entityIdx.Index);
            }
        }

        /// <summary>
        /// Batch queue move operations from pre-sorted native data.
        /// Groups entities by (fromGroup, toGroup) to amortize dictionary lookups.
        /// </summary>
        public void QueueNativeMoveOperations(
            NativeList<(EntityIndex, Group, int)> sortedSwaps,
            WorldInfo worldInfo
        )
        {
            Group cachedFromGroup = default;
            Group cachedToGroup = default;
            DenseDictionary<Group, DenseDictionary<int, MoveInfo>> cachedFromGroupDict = null;
            DenseDictionary<int, MoveInfo> cachedToGroupDict = null;

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

                if (fromEntityIndex.Group != cachedFromGroup)
                {
                    cachedFromGroup = fromEntityIndex.Group;
                    cachedFromGroupDict =
                        _thisSubmissionInfo._currentSwapEntitiesOperations.RecycleOrAdd(
                            cachedFromGroup,
                            _newGroupDictionary,
                            _recycleDicitionaryWithCaller
                        );
                    // Reset toGroup cache when fromGroup changes
                    cachedToGroup = default;
                    cachedToGroupDict = null;
                }

                if (toGroup != cachedToGroup)
                {
                    cachedToGroup = toGroup;
                    cachedToGroupDict = cachedFromGroupDict.RecycleOrAdd(
                        cachedToGroup,
                        _newListWithCaller,
                        _clearListWithCaller
                    );
                }

                cachedToGroupDict.Add(fromEntityIndex.Index, default);
            }
        }

        /// <summary>
        /// After a move operation's swap-back changes entity positions in a group,
        /// update any pending remove indices for that group so they target the correct positions.
        /// </summary>
        internal void UpdateRemoveIndicesAfterMoveSwapBack(
            Group fromGroup,
            DenseDictionary<int, int> swapBackMapping
        )
        {
            if (
                !_lastSubmittedInfo._currentRemoveEntitiesOperations.TryGetValue(
                    fromGroup,
                    out var removeList
                )
            )
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
                    Assert.That(++hops <= maxHops, "Cycle detected in swap-back mapping");
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
                DenseDictionary<Group, DenseDictionary<Group, DenseDictionary<int, MoveInfo>>>,
                DenseDictionary<EntityIndex, (EntityIndex, Group)>,
                EntitySubmitter
            > moveEntities,
            Action<DenseDictionary<Group, FastList<int>>, EntitySubmitter> removeEntities,
            Action<Group, EntitySubmitter> removeGroup,
            Action<Group, Group, EntitySubmitter> swapGroup,
            EntitySubmitter ecsRoot
        )
        {
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

            if (_lastSubmittedInfo._currentSwapEntitiesOperations.Count > 0)
            {
                moveEntities(
                    _lastSubmittedInfo._currentSwapEntitiesOperations,
                    _lastSubmittedInfo._entitiesMoved,
                    ecsRoot
                );
            }

            if (_lastSubmittedInfo._currentRemoveEntitiesOperations.Count > 0)
            {
                removeEntities(_lastSubmittedInfo._currentRemoveEntitiesOperations, ecsRoot);
            }

            using (TrecsProfiling.Start("Clear Submitted Info"))
            {
                _lastSubmittedInfo.Clear();
            }
        }

        static FastList<int> NewList()
        {
            return new FastList<int>();
        }

        static void ClearList(ref FastList<int> target)
        {
            target.Clear();
        }

        static void RecycleDicitionaryWithCaller(
            ref DenseDictionary<Group, DenseDictionary<int, MoveInfo>> target
        )
        {
            target.Recycle();
        }

        static void ClearListWithCaller(ref DenseDictionary<int, MoveInfo> target)
        {
            target.Recycle();
        }

        static DenseDictionary<int, MoveInfo> NewListWithCaller()
        {
            return new DenseDictionary<int, MoveInfo>();
        }

        static DenseDictionary<Group, DenseDictionary<int, MoveInfo>> NewGroupDictionary()
        {
            return new DenseDictionary<Group, DenseDictionary<int, MoveInfo>>();
        }

        struct Info
        {
            //from group         //actual component type
            internal DenseDictionary<
                Group,
                DenseDictionary<Group, DenseDictionary<int, MoveInfo>>
            > _currentSwapEntitiesOperations;

            internal DenseDictionary<Group, FastList<int>> _currentRemoveEntitiesOperations;

            internal DenseDictionary<
                EntityIndex,
                (EntityIndex fromEntityIndex, Group toGroup)
            > _entitiesMoved;
            internal DenseHashSet<EntityIndex> _entitiesRemoved;
            public FastList<(Group, Group, string)> _groupsToMove;
            public FastList<(Group, string)> _groupsToRemove;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal bool AnyOperationQueued()
            {
                return _entitiesMoved.Count > 0
                    || _entitiesRemoved.Count > 0
                    || _currentSwapEntitiesOperations.Count > 0
                    || _currentRemoveEntitiesOperations.Count > 0
                    || _groupsToMove.Count > 0
                    || _groupsToRemove.Count > 0;
            }

            internal void Clear()
            {
                _currentSwapEntitiesOperations.Recycle();
                _currentRemoveEntitiesOperations.Recycle();
                _entitiesMoved.Recycle();
                _entitiesRemoved.Recycle();
                _groupsToRemove.Clear();
                _groupsToMove.Clear();
            }

            internal void Init()
            {
                _entitiesMoved =
                    new DenseDictionary<
                        EntityIndex,
                        (EntityIndex fromEntityIndex, Group toGroup)
                    >();
                _entitiesRemoved = new DenseHashSet<EntityIndex>();
                _groupsToRemove = new FastList<(Group, string)>();
                _groupsToMove = new FastList<(Group, Group, string)>();

                _currentSwapEntitiesOperations =
                    new DenseDictionary<
                        Group,
                        DenseDictionary<Group, DenseDictionary<int, MoveInfo>>
                    >();
                _currentRemoveEntitiesOperations = new DenseDictionary<Group, FastList<int>>();
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
