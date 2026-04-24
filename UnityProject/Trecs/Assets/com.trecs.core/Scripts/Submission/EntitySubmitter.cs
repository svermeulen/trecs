using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Jobs;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public class EntitySubmitter : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(EntitySubmitter));

        readonly EventsManager _eventsManager;
        bool _isDisposed;

        readonly DoubleBufferedEntitiesToAdd _groupedEntityToAdd;
        readonly EntitiesOperations _entitiesOperations;

        //transient caches>>>>>>>>>>>>>>>>>>>>>
        readonly FastList<EntityRange> _cachedRangeOfSubmittedIndices;
        readonly DenseDictionary<int, int> _transientEntityIDsAffectedByRemoveAtSwapBack;
        NativeList<int> _cachedSortedDescendingRemoveIndices;
        readonly FastList<IComponentArray> _cachedSrcArrays;
        readonly FastList<IComponentArray> _cachedDstArrays;

        // Cached array for native add component lookups to avoid per-frame allocation
        IComponentArray[] _cachedNativeAddComponentArrays;

        // Minimum entities * components to justify parallel job scheduling overhead.
        // At small batch sizes (DoofusDemo: ~10 entities × 5 components = 50), the job
        // scheduling overhead exceeds the parallelism benefit. Only parallelize for
        // larger workloads where the data movement dominates scheduling cost.
        // TODO: This value was set heuristically. Benchmark across different hardware
        // to find the actual crossover point where parallel scheduling overhead is recovered.
        const int ParallelJobThreshold = 500;

        static readonly Action<
            DenseDictionary<
                GroupIndex,
                DenseDictionary<GroupIndex, DenseDictionary<int, MoveInfo>>
            >,
            DenseDictionary<EntityIndex, (EntityIndex, GroupIndex)>,
            EntitySubmitter
        > _moveEntities;

        static readonly Action<
            DenseDictionary<GroupIndex, FastList<int>>,
            EntitySubmitter
        > _removeEntities;

        static readonly Action<GroupIndex, EntitySubmitter> _removeGroup;
        static readonly Action<GroupIndex, GroupIndex, EntitySubmitter> _swapGroup;

        internal readonly ComponentStore _componentStore;
        readonly EntityQuerier _entitiesQuerier;

        DenseDictionary<EntityIndex, OperationType> _multipleOperationOnSameEntityChecker;

        internal readonly SetStore _setStore;

        readonly SimpleSubject _submitCompleteEvent = new();
        readonly SimpleSubject _submitStartedEvent = new();

        readonly NativeSharedHeap _nativeSharedHeap;
        readonly NativeUniqueHeap _nativeUniqueHeap;
        readonly FrameScopedNativeUniqueHeap _frameScopedNativeUniqueHeap;

        readonly RuntimeJobScheduler _jobScheduler;

        readonly AtomicNativeBags _nativeAddOperationQueue;
        readonly AtomicNativeBags _nativeRemoveOperationQueue;
        readonly AtomicNativeBags _nativeMoveOperationQueue;

        // Per-set deferred bags are now on EntitySet; no centralized set queues needed.
        readonly WorldInfo _worldInfo;
        readonly WorldAccessorRegistry _accessorRegistry;
        readonly WorldSettings _trecsSettings;

#if DEBUG && TRECS_IS_PROFILING
        internal readonly HashSet<GroupIndex> _groupsWithEntitiesEverAdded = new();
#endif
#if DEBUG && !TRECS_IS_PROFILING
        readonly EntityInitializationTracker _initTracker = new();
#endif

        bool _isRunningSubmit;

        static EntitySubmitter()
        {
            _moveEntities = MoveEntities;
            _removeEntities = RemoveEntities;
            _removeGroup = RemoveGroup;
            _swapGroup = SwapGroup;
        }

        public ISimpleObservable SubmissionCompletedEvent
        {
            get { return _submitCompleteEvent; }
        }

        public ISimpleObservable SubmissionStartedEvent
        {
            get { return _submitStartedEvent; }
        }

        public EntitySubmitter(
            WorldInfo worldInfo,
            WorldAccessorRegistry accessorRegistry,
            EventsManager eventsManager,
            ComponentStore componentStore,
            SetStore setStore,
            WorldSettings trecsSettings,
            EntityQuerier entitiesQuerier,
            NativeSharedHeap nativeSharedHeap,
            NativeUniqueHeap nativeUniqueHeap,
            FrameScopedNativeUniqueHeap frameScopedNativeUniqueHeap,
            RuntimeJobScheduler jobScheduler
        )
        {
            _entitiesOperations = new EntitiesOperations();

            _jobScheduler = jobScheduler;
            _nativeSharedHeap = nativeSharedHeap;
            _nativeUniqueHeap = nativeUniqueHeap;
            _frameScopedNativeUniqueHeap = frameScopedNativeUniqueHeap;
            _cachedRangeOfSubmittedIndices = new FastList<EntityRange>();
            _transientEntityIDsAffectedByRemoveAtSwapBack = new DenseDictionary<int, int>();
            _cachedSortedDescendingRemoveIndices = new NativeList<int>(16, Allocator.Persistent);
            _cachedSrcArrays = new FastList<IComponentArray>();
            _cachedDstArrays = new FastList<IComponentArray>();
            _worldInfo = worldInfo;
            _accessorRegistry = accessorRegistry;
            _trecsSettings = trecsSettings ?? new WorldSettings();

            InitStructuralChangeChecks();
            _nativeAddOperationQueue = AtomicNativeBags.Create();
            _nativeRemoveOperationQueue = AtomicNativeBags.Create();
            _nativeMoveOperationQueue = AtomicNativeBags.Create();
            // Per-set deferred bags are allocated in EntitySet.
            _eventsManager = eventsManager;

            _componentStore = componentStore;
            _groupedEntityToAdd = new DoubleBufferedEntitiesToAdd();
            _setStore = setStore;

            _entitiesQuerier = entitiesQuerier;
        }

        public bool ConfigurationFrozen
        {
            get { return _componentStore.ConfigurationFrozen; }
        }

        public bool IsValid()
        {
            return !_isDisposed;
        }

        public void SubmitEntities()
        {
            _submitStartedEvent.Invoke();
            SubmitEntitiesImpl();
            _submitCompleteEvent.Invoke();
        }

        void FlushAllDeferredOps()
        {
            // Deferred heap flushes apply swap-back removals to containers that
            // jobs may have been reading from via the resolver. Catch misuse
            // (flush called mid-job) loudly in DEBUG rather than producing
            // corrupted reads.
            Assert.That(
                !_jobScheduler.HasOutstandingJobs,
                "FlushAllDeferredOps called while jobs are still outstanding. "
                    + "Call scheduler.CompleteAllOutstanding() first."
            );

            _nativeSharedHeap.FlushPendingOperations();
            _nativeUniqueHeap.FlushPendingOperations();
            _frameScopedNativeUniqueHeap.FlushPendingOperations();

            using (TrecsProfiling.Start("Deferred Set Operations"))
            {
                _setStore.FlushAllDeferredOps(_trecsSettings.RequireDeterministicSubmission);
            }

            FlushNativeOperations();
        }

        void SubmitEntitiesImpl()
        {
            _eventsManager.NotifyOnSubmissionStarted();

            Assert.That(
                !_isRunningSubmit,
                "A submission started while the previous one was still flushing"
            );
            _isRunningSubmit = true;

            using (TrecsProfiling.Start("Entities Submission"))
            {
                var iterations = 0;
                var hasEverSubmitted = false;

                FlushAllDeferredOps();

                while (
                    HasMadeNewStructuralChangesInThisIteration()
                    && iterations++ < _trecsSettings.MaxSubmissionIterations
                )
                {
                    hasEverSubmitted = true;

                    SingleSubmission();
                    FlushAllDeferredOps();
                }

#if DEBUG
                if (iterations == _trecsSettings.MaxSubmissionIterations)
                    throw new TrecsException("possible circular submission detected");
#endif
                if (hasEverSubmitted)
                {
                    using (TrecsProfiling.Start("NotifyOnSubmission"))
                    {
                        _eventsManager.NotifyOnSubmission();
                    }
                }
            }

            _isRunningSubmit = false;
        }

        public void Dispose()
        {
            Assert.That(!_isDisposed);
            _isDisposed = true;

            using (TrecsProfiling.Start("Final Dispose"))
            {
                _componentStore.Dispose();

                _setStore.Dispose();

                _nativeAddOperationQueue.Dispose();
                _nativeRemoveOperationQueue.Dispose();
                _nativeMoveOperationQueue.Dispose();
                // Per-set deferred bags are disposed by SetStore.

                _eventsManager.Dispose();

                _groupedEntityToAdd.Dispose();

                _entitiesQuerier._entityLocator.Dispose();

                if (_cachedSortedDescendingRemoveIndices.IsCreated)
                    _cachedSortedDescendingRemoveIndices.Dispose();

                Assert.That(_submitCompleteEvent.NumObservers == 0);
                Assert.That(_submitStartedEvent.NumObservers == 0);

#if DEBUG && !TRECS_IS_PROFILING
                _initTracker.Clear();
#endif
            }
        }

        ///--------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityInitializer AddEntity(
            GroupIndex group,
            IComponentBuilder[] componentsToBuild,
            string descriptorName,
            string callerFile = "",
            int callerLine = 0
        )
        {
#if DEBUG && TRECS_IS_PROFILING
            _groupsWithEntitiesEverAdded.Add(group);
#endif
#if DEBUG && !TRECS_IS_PROFILING
            var trackingId = _initTracker.Register(
                group,
                componentsToBuild,
                descriptorName,
                callerFile,
                callerLine
            );
#endif

            var reference = _entitiesQuerier._entityLocator.ClaimId();

            var (dic, insertionIndex) = EntityFactory.BuildGroupedEntities(
                group,
                _groupedEntityToAdd,
                componentsToBuild
#if DEBUG
                ,
                descriptorName
#endif
            );

            // Defer SetEntityHandle to submission time when the actual DB index is known.
            // Store the reference so AddEntities() can call SetEntityHandle with correct indices.
            _groupedEntityToAdd.AddPendingReference(group, reference);

            return new EntityInitializer(
                group,
                dic,
                reference,
                insertionIndex
#if DEBUG && !TRECS_IS_PROFILING
                ,
                _initTracker,
                trackingId
#endif
            );
        }

        internal void FreezeConfiguration()
        {
            _groupedEntityToAdd.FreezeConfiguration();
            _entitiesQuerier._entityLocator.FreezeConfiguration();
            _componentStore.FreezeConfiguration();
        }

        /// <summary>
        /// Preallocate memory to avoid the impact to resize arrays when many entities are submitted at once
        /// </summary>
        internal void Preallocate(
            GroupIndex groupId,
            int size,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
            Assert.That(!_componentStore.ConfigurationFrozen);

            using (TrecsProfiling.Start("PreallocateDBGroup"))
            {
                _componentStore.PreallocateDBGroup(groupId, size, entityComponentsToBuild);
            }

            using (TrecsProfiling.Start("PreallocateEntitiesToAdd"))
            {
                _groupedEntityToAdd.Preallocate(groupId, size, entityComponentsToBuild);
            }

            using (TrecsProfiling.Start("PreallocateIdMaps"))
            {
                _entitiesQuerier._entityLocator.PreallocateIdMaps(groupId, size);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal DenseDictionary<ComponentId, IComponentArray> GetDBGroup(GroupIndex fromIdGroupId)
        {
            return _componentStore.GetDBGroup(fromIdGroupId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void QueueRemoveGroupOperation(GroupIndex groupId, string caller)
        {
            _entitiesOperations.QueueRemoveGroupOperation(groupId, caller);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void QueueMoveGroupOperation(GroupIndex fromGroupId, GroupIndex toGroupId, string caller)
        {
            _entitiesOperations.QueueMoveGroupOperation(fromGroupId, toGroupId, caller);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueMoveEntityOperation(
            EntityIndex fromId,
            GroupIndex toGroup,
            IComponentBuilder[] componentBuilders
        )
        {
            _entitiesOperations.QueueMoveOperation(fromId, toGroup, componentBuilders);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsScheduledForRemove(EntityIndex entityIndex)
        {
            return _entitiesOperations.IsScheduledForRemove(entityIndex);
        }

        /// <summary>
        /// Note that this does not account for swaps that are scheduled natively!
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsScheduledForMove(EntityIndex entityIndex)
        {
            return _entitiesOperations.IsScheduledForMove(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueRemoveEntityOperation(
            EntityIndex entityHandlex,
            IComponentBuilder[] componentBuilders
        )
        {
            _entitiesOperations.QueueRemoveOperation(entityHandlex, componentBuilders);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueRemoveAllInGroup(GroupIndex group, int entityCount)
        {
            _entitiesOperations.QueueRemoveAllInGroup(group, entityCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SingleSubmission()
        {
            //clear the data checks before the submission. We want to allow structural changes inside the callbacks
            using (TrecsProfiling.Start("ClearMultiOpCheck Pre"))
            {
                ClearChecksForMultipleOperationsOnTheSameEntity();
            }

            _entitiesOperations.ExecuteRemoveAndSwappingOperations(
                _moveEntities,
                _removeEntities,
                _removeGroup,
                _swapGroup,
                this
            );

            AddEntities();

            //clear the data checks after the submission, so if structural changes happened inside the callback, the debug structure is reset for the next frame operations
            using (TrecsProfiling.Start("ClearMultiOpCheck Post"))
            {
                ClearChecksForMultipleOperationsOnTheSameEntity();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void RemoveGroup(GroupIndex groupId, EntitySubmitter ecsRoot)
        {
            using (TrecsProfiling.Start("remove whole group"))
            {
                ecsRoot.RemoveEntitiesFromGroup(groupId);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void SwapGroup(GroupIndex fromGroupId, GroupIndex toGroupId, EntitySubmitter ecsRoot)
        {
            using (TrecsProfiling.Start("swap whole group"))
            {
                ecsRoot.SwapEntitiesBetweenGroups(fromGroupId, toGroupId);
            }
        }

        static void RemoveEntities(
            DenseDictionary<GroupIndex, FastList<int>> removeOperations,
            EntitySubmitter ecsRoot
        )
        {
            using (TrecsProfiling.Start("remove Entities"))
            {
                ecsRoot._cachedRangeOfSubmittedIndices.Clear();

                foreach (var entitiesToRemove in removeOperations)
                {
                    ExecuteRemoveForGroup(entitiesToRemove.Key, entitiesToRemove.Value, ecsRoot);
                }

                FireRemoveCallbacks(removeOperations, ecsRoot);
            }
        }

        static void ExecuteRemoveForGroup(
            GroupIndex fromGroup,
            FastList<int> entityHandlesToRemove,
            EntitySubmitter ecsRoot
        )
        {
            var fromGroupDictionary = ecsRoot.GetDBGroup(fromGroup);

            if (fromGroupDictionary.Count == 0)
            {
                return;
            }

            // R1: Sort removal indices descending and compute swap-back plan once.
            // Descending order ensures swap-back never invalidates a future removal index,
            // eliminating chain resolution. The plan is identical for all component types
            // since they are parallel arrays of the same length.
            var sortedIndices = ecsRoot._cachedSortedDescendingRemoveIndices;
            sortedIndices.Clear();
            for (int i = 0; i < entityHandlesToRemove.Count; i++)
            {
                sortedIndices.Add(entityHandlesToRemove[i]);
            }

            var originalCount = GetGroupEntityCount(fromGroupDictionary);

            using (TrecsProfiling.Start("Remove Sort+Plan"))
            {
                SortDescending(sortedIndices);

                ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack.Recycle();
                ComputeSwapBackPlanDescending(
                    sortedIndices,
                    originalCount,
                    ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack
                );
            }

            // R2: Apply swap-back to all component arrays via Burst job.
            var hasFilters = ecsRoot._setStore.HasAnySets;
            var numComponents = fromGroupDictionary.Count;
            var numRemovals = sortedIndices.Length;

            using (TrecsProfiling.Start("Remove Setup+Job"))
            {
                // Gather unsafe pointers for each component type
                var arrayPtrs = new NativeArray<long>(numComponents, Allocator.TempJob);
                var elemSizes = new NativeArray<int>(numComponents, Allocator.TempJob);
                int maxElemSize = 0;
                int compIdx = 0;

                foreach (var (_, fromComponentsDictionary) in fromGroupDictionary)
                {
                    unsafe
                    {
                        arrayPtrs[compIdx] = (long)fromComponentsDictionary.GetUnsafePtr();
                    }
                    var es = fromComponentsDictionary.ElementSize;
                    elemSizes[compIdx] = es;
                    if (es > maxElemSize)
                    {
                        maxElemSize = es;
                    }
                    compIdx++;
                }

                // Zero-copy view of the sorted indices for the Burst job
                var nativeSortedIndices = sortedIndices.AsArray().GetSubArray(0, numRemovals);

                // Execute Burst job(s) for data movement
                if (numRemovals * numComponents >= ParallelJobThreshold && numComponents > 1)
                {
                    // Schedule one job per component type in parallel
                    var handles = new NativeArray<JobHandle>(numComponents, Allocator.TempJob);
                    for (int ci = 0; ci < numComponents; ci++)
                    {
                        var perCompJob = new RemoveDataPerComponentJob
                        {
                            ArrayPtr = arrayPtrs[ci],
                            ElementSize = elemSizes[ci],
                            SourceCount = originalCount,
                            NumRemovals = numRemovals,
                            SortedDescendingIndices = nativeSortedIndices,
                        };
                        handles[ci] = perCompJob.Schedule();
                    }
                    var combined = JobHandle.CombineDependencies(
                        handles.GetSubArray(0, numComponents)
                    );
                    combined.Complete();
                    handles.Dispose();
                }
                else
                {
                    // Single job for small batches
                    var removeJob = new RemoveDataJob
                    {
                        ArrayPtrs = arrayPtrs,
                        ElementSizes = elemSizes,
                        SortedDescendingIndices = nativeSortedIndices,
                        NumComponents = numComponents,
                        NumRemovals = numRemovals,
                        SourceCount = originalCount,
                        MaxElementSize = maxElemSize,
                    };
                    removeJob.Run();
                }

                // Update managed counts
                var newCount = originalCount - numRemovals;
                foreach (var (_, fromComponentsDictionary) in fromGroupDictionary)
                {
                    fromComponentsDictionary.SetCount(newCount);
                }

                var entityIndicesRange = new EntityRange(newCount, originalCount);
                ecsRoot._cachedRangeOfSubmittedIndices.Add(entityIndicesRange);

                arrayPtrs.Dispose();
                elemSizes.Dispose();
            }

            if (hasFilters)
            {
                using (TrecsProfiling.Start("Remove Filters"))
                {
                    ecsRoot._setStore.RemoveEntitiesFromSets(
                        entityHandlesToRemove,
                        fromGroup,
                        ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack
                    );
                }
            }

            // R3: Update entity references (batched).
            // RemoveEntityHandle must happen BEFORE UpdateIndexAfterSwapBack:
            // after swap-back, entity Y moves into removed entity X's slot. If we called
            // UpdateIndexAfterSwapBack first, it would move Y's reverse map entry to X's old key,
            // then RemoveEntityHandle(X) would incorrectly remove Y's entry.
            using (TrecsProfiling.Start("Remove Refs"))
            {
                ecsRoot._entitiesQuerier._entityLocator.BatchRemoveEntityHandles(
                    entityHandlesToRemove,
                    fromGroup
                );

                ecsRoot._entitiesQuerier._entityLocator.BatchUpdateIndexAfterSwapBack(
                    ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack,
                    fromGroup
                );
            }

            var postRemoveCount = originalCount - numRemovals;
            ecsRoot._entitiesQuerier._entityLocator.TrimGroupList(fromGroup, postRemoveCount);

#if TRECS_INTERNAL_CHECKS && DEBUG
            ecsRoot._entitiesQuerier._entityLocator.ValidateGroupConsistency(
                fromGroup,
                postRemoveCount
            );
#endif
        }

        /// <summary>
        /// Compute the swap-back mapping for removals processed in descending index order.
        /// For each removal, the entity at the current last index swaps into the removed slot.
        /// </summary>
        static void ComputeSwapBackPlanDescending(
            NativeList<int> sortedDescendingIndices,
            int originalCount,
            DenseDictionary<int, int> swapBackMapping
        )
        {
            var currentCount = originalCount;
            for (int i = 0; i < sortedDescendingIndices.Length; i++)
            {
                currentCount--;
                var removedIndex = sortedDescendingIndices[i];
                if (removedIndex != currentCount)
                {
                    swapBackMapping[currentCount] = removedIndex;
                }
            }
        }

        static void FireRemoveCallbacks(
            DenseDictionary<GroupIndex, FastList<int>> removeOperations,
            EntitySubmitter ecsRoot
        )
        {
            var rangeEnumerator = ecsRoot._cachedRangeOfSubmittedIndices.GetEnumerator();

            //Note, very important: This is exploiting a trick of the removal operation (RemoveEntitiesFromArray)
            //You may wonder: how can the remove callbacks iterate entities that have been just removed
            //from the database? This works just because during a remove, entities are put at the end of the
            //array and not actually removed. The entities are not iterated anymore in future just because
            //the count of the array decreases. This means that at the end of the array, after the remove
            //operations, we will find the collection of entities just removed. The remove callbacks are
            //going to iterate the array from the new count to the new count + the number of entities removed
            using (TrecsProfiling.Start("Execute remove Callbacks Fast"))
            {
                foreach (var (group, _) in removeOperations)
                {
                    var fromGroupDictionary = ecsRoot.GetDBGroup(group);

                    if (fromGroupDictionary.Count == 0)
                    {
                        continue;
                    }

                    rangeEnumerator.MoveNext();

                    if (
                        ecsRoot._eventsManager.ReactiveOnRemovedObservers.TryGetValue(
                            group,
                            out var groupRemovedObservers
                        )
                    )
                    {
                        for (var i = 0; i < groupRemovedObservers.Count; i++)
                        {
                            groupRemovedObservers[i].Observer(group, rangeEnumerator.Current);
                        }
                    }
                }
            }
        }

        static void SortDescending(NativeList<int> list)
        {
            list.Sort(new DescendingIntComparer());
        }

        struct DescendingIntComparer : IComparer<int>
        {
            public int Compare(int a, int b) => b.CompareTo(a);
        }

        /// <summary>
        /// Get the entity count for a group from any of its component arrays
        /// (all component types are parallel arrays with the same count).
        /// </summary>
        static int GetGroupEntityCount(
            DenseDictionary<ComponentId, IComponentArray> groupDictionary
        )
        {
            foreach (var (_, componentArray) in groupDictionary)
            {
                return componentArray.Count;
            }

            return 0;
        }

        static void MoveEntities(
            DenseDictionary<
                GroupIndex,
                DenseDictionary<GroupIndex, DenseDictionary<int, MoveInfo>>
            > moveEntitiesOperations,
            DenseDictionary<EntityIndex, (EntityIndex, GroupIndex)> entitiesIdSwaps,
            EntitySubmitter ecsRoot
        )
        {
            using (TrecsProfiling.Start("Swap entities between groups"))
            {
                foreach (var (fromGroup, toGroupMoveInfos) in moveEntitiesOperations)
                {
                    ecsRoot._cachedRangeOfSubmittedIndices.Clear();

                    using (TrecsProfiling.Start("Move RecycleSwapBack"))
                    {
                        ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack.Recycle();
                    }

                    DenseDictionary<ComponentId, IComponentArray> fromGroupDictionary =
                        ecsRoot.GetDBGroup(fromGroup);

                    var sourceCount = GetGroupEntityCount(fromGroupDictionary);

                    // Precompute resolved indices and execute data movement per toGroup.
                    // These are merged into a single loop because Burst jobs complete
                    // synchronously and the swap-back chain is built incrementally.
                    using (TrecsProfiling.Start("Move Precompute+Execute"))
                    {
                        foreach (var (toGroup, fromEntityToEntityIDs) in toGroupMoveInfos)
                        {
                            if (fromEntityToEntityIDs.Count == 0)
                                continue;

                            PrecomputeMoveResolvedIndices(
                                fromEntityToEntityIDs,
                                ref sourceCount,
                                ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack
                            );

                            ExecuteMoveForToGroup(
                                fromGroup,
                                toGroup,
                                fromEntityToEntityIDs,
                                fromGroupDictionary,
                                ecsRoot
                            );
                        }
                    }

                    // Update EntityHandleMap for entities in the source group whose indices
                    // changed due to swap-back (entities that stayed but got moved to a new slot).
                    // Done once per fromGroup after all toGroups are processed.
                    using (TrecsProfiling.Start("Move SwapBack Refs"))
                    {
                        ecsRoot._entitiesQuerier._entityLocator.BatchUpdateIndexAfterSwapBack(
                            ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack,
                            fromGroup
                        );

                        // Update any pending remove indices for this group to account for
                        // positions that changed due to the move's swap-back.
                        ecsRoot._entitiesOperations.UpdateRemoveIndicesAfterMoveSwapBack(
                            fromGroup,
                            ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack
                        );
                    }

                    var postMoveCount = GetGroupEntityCount(fromGroupDictionary);
                    ecsRoot._entitiesQuerier._entityLocator.TrimGroupList(fromGroup, postMoveCount);

                    FireMoveCallbacks(fromGroup, toGroupMoveInfos, ecsRoot);

#if TRECS_INTERNAL_CHECKS && DEBUG
                    ecsRoot._entitiesQuerier._entityLocator.ValidateGroupConsistency(
                        fromGroup,
                        postMoveCount
                    );
#endif
                }
            }
        }

        /// <summary>
        /// Precompute the resolved source index for each entity to move, accounting for
        /// prior swap-backs from earlier toGroups. This is done once per toGroup so that
        /// the component-type loop can skip chain resolution entirely.
        /// </summary>
        static void PrecomputeMoveResolvedIndices(
            DenseDictionary<int, MoveInfo> fromEntityToEntityIDs,
            ref int sourceCount,
            DenseDictionary<int, int> swapBackDict
        )
        {
            var keys = fromEntityToEntityIDs.UnsafeKeys;
            var values = fromEntityToEntityIDs.UnsafeValues;

            for (int i = 0; i < fromEntityToEntityIDs.Count; i++)
            {
                var originalIndex = keys[i].key;

                // Resolve chain: follow swap-back entries to find where this entity actually is
                var resolvedIndex = originalIndex;
                while (swapBackDict.TryGetValue(resolvedIndex, out var updated))
                {
                    resolvedIndex = updated;
                }

                values[i].ResolvedFromIndex = resolvedIndex;

                // Record swap-back for subsequent entities
                sourceCount--;
                if (resolvedIndex != sourceCount)
                {
                    swapBackDict[sourceCount] = resolvedIndex;
                }
            }
        }

        static void ExecuteMoveForToGroup(
            GroupIndex fromGroup,
            GroupIndex toGroup,
            DenseDictionary<int, MoveInfo> fromEntityToEntityIDs,
            DenseDictionary<ComponentId, IComponentArray> fromGroupDictionary,
            EntitySubmitter ecsRoot
        )
        {
            var numEntities = fromEntityToEntityIDs.Count;
            Assert.That(numEntities > 0, "something went wrong, no entities to swap");

            var toGroupDB = ecsRoot.GetDBGroup(toGroup);
            var hasFilters = ecsRoot._setStore.HasAnySets;
            var numComponents = fromGroupDictionary.Count;

            // Phase 1: Managed setup - gather unsafe pointers and ensure capacity.
            using (TrecsProfiling.Start("Move Setup+Job"))
            {
                var srcPtrs = new NativeArray<long>(numComponents, Allocator.TempJob);
                var dstPtrs = new NativeArray<long>(numComponents, Allocator.TempJob);
                var elementSizes = new NativeArray<int>(numComponents, Allocator.TempJob);
                var dstBaseCounts = new NativeArray<int>(numComponents, Allocator.TempJob);
                var srcCounts = new NativeArray<int>(numComponents, Allocator.TempJob);
                ecsRoot._cachedSrcArrays.Clear();
                ecsRoot._cachedDstArrays.Clear();

                EntityRange? toGroupIndexRange = null;
                int compIdx = 0;

                foreach (var (componentType, fromComponentsDictionaryDB) in fromGroupDictionary)
                {
                    IComponentArray toComponentsDictionaryDB = ecsRoot.GetOrAddTypeSafeDictionary(
                        toGroup,
                        toGroupDB,
                        componentType,
                        fromComponentsDictionaryDB
                    );

                    Assert.That(
                        toComponentsDictionaryDB != null,
                        "something went wrong with the creation of dictionaries"
                    );

                    var newBufferSize = toComponentsDictionaryDB.Count + numEntities;
                    toComponentsDictionaryDB.EnsureCapacity(newBufferSize);

                    var componentIndexRange = new EntityRange(
                        toComponentsDictionaryDB.Count,
                        newBufferSize
                    );

                    if (toGroupIndexRange.HasValue)
                    {
                        Assert.That(toGroupIndexRange.Value == componentIndexRange);
                    }
                    else
                    {
                        toGroupIndexRange = componentIndexRange;
                    }

                    unsafe
                    {
                        srcPtrs[compIdx] = (long)fromComponentsDictionaryDB.GetUnsafePtr();
                        dstPtrs[compIdx] = (long)toComponentsDictionaryDB.GetUnsafePtr();
                    }
                    elementSizes[compIdx] = fromComponentsDictionaryDB.ElementSize;
                    dstBaseCounts[compIdx] = toComponentsDictionaryDB.Count;
                    srcCounts[compIdx] = fromComponentsDictionaryDB.Count;
                    ecsRoot._cachedSrcArrays.Add(fromComponentsDictionaryDB);
                    ecsRoot._cachedDstArrays.Add(toComponentsDictionaryDB);

                    compIdx++;
                }

                // Build resolved indices array for the Burst job
                var resolvedFromIndices = new NativeArray<int>(numEntities, Allocator.TempJob);
                var swapValues = fromEntityToEntityIDs.UnsafeValues;
                for (int i = 0; i < numEntities; i++)
                {
                    resolvedFromIndices[i] = swapValues[i].ResolvedFromIndex;
                }

                // Pre-set toIndex on MoveInfo (sequential from destBase, known before data movement)
                var destBase = toGroupIndexRange.Value.Start;
                for (int i = 0; i < numEntities; i++)
                {
                    swapValues[i].ToIndex = destBase + i;
                }

                // Phase 2: Burst job(s) for data movement
                var sourceCount = srcCounts[0];

                if (numEntities * numComponents >= ParallelJobThreshold && numComponents > 1)
                {
                    // Schedule one job per component type in parallel
                    var handles = new NativeArray<JobHandle>(numComponents, Allocator.TempJob);
                    for (int ci = 0; ci < numComponents; ci++)
                    {
                        var perCompJob = new MoveDataPerComponentJob
                        {
                            SrcPtr = srcPtrs[ci],
                            DstPtr = dstPtrs[ci],
                            ElementSize = elementSizes[ci],
                            DstBaseCount = dstBaseCounts[ci],
                            SourceCount = sourceCount,
                            NumEntities = numEntities,
                            ResolvedFromIndices = resolvedFromIndices,
                        };
                        handles[ci] = perCompJob.Schedule();
                    }
                    var combined = JobHandle.CombineDependencies(
                        handles.GetSubArray(0, numComponents)
                    );
                    combined.Complete();
                    handles.Dispose();
                }
                else
                {
                    // Single job for small batches (avoids scheduling overhead)
                    var job = new MoveDataJob
                    {
                        SrcPtrs = srcPtrs,
                        DstPtrs = dstPtrs,
                        ElementSizes = elementSizes,
                        DstBaseCounts = dstBaseCounts,
                        ResolvedFromIndices = resolvedFromIndices,
                        NumComponents = numComponents,
                        NumEntities = numEntities,
                        SourceCount = sourceCount,
                    };
                    job.Run();
                }

                // Phase 3: Update managed counts
                for (int ci = 0; ci < numComponents; ci++)
                {
                    ecsRoot._cachedSrcArrays[ci].SetCount(srcCounts[ci] - numEntities);
                    ecsRoot._cachedDstArrays[ci].SetCount(dstBaseCounts[ci] + numEntities);
                }

                Assert.That(toGroupIndexRange.HasValue);
                ecsRoot._cachedRangeOfSubmittedIndices.Add(toGroupIndexRange.Value);

                srcPtrs.Dispose();
                dstPtrs.Dispose();
                elementSizes.Dispose();
                dstBaseCounts.Dispose();
                srcCounts.Dispose();
                resolvedFromIndices.Dispose();
            }

            // Phase 4: Set updates
            if (hasFilters)
            {
                using (TrecsProfiling.Start("Move Filters"))
                {
                    ecsRoot._setStore.SwapEntityBetweenSets(
                        fromEntityToEntityIDs,
                        fromGroup,
                        toGroup,
                        ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack
                    );
                }
            }

            // Phase 5: Update entity references (managed, batched)
            using (TrecsProfiling.Start("Move Refs"))
            {
                ecsRoot._entitiesQuerier._entityLocator.BatchUpdateEntityHandles(
                    fromEntityToEntityIDs,
                    fromGroup,
                    toGroup
                );
            }
        }

        static void FireMoveCallbacks(
            GroupIndex fromGroup,
            DenseDictionary<GroupIndex, DenseDictionary<int, MoveInfo>> toGroupMoveInfos,
            EntitySubmitter ecsRoot
        )
        {
            var rangeEnumerator = ecsRoot._cachedRangeOfSubmittedIndices.GetEnumerator();

            using (TrecsProfiling.Start("Execute Swap Callbacks Fast"))
            {
                foreach (var (toGroup, fromEntityToEntityIDs) in toGroupMoveInfos)
                {
                    if (fromEntityToEntityIDs.Count == 0)
                        continue;

                    rangeEnumerator.MoveNext();

                    if (
                        ecsRoot._eventsManager.ReactiveOnMovedObservers.TryGetValue(
                            toGroup,
                            out var groupSwappedObservers
                        )
                    )
                    {
                        for (var i = 0; i < groupSwappedObservers.Count; i++)
                        {
                            groupSwappedObservers[i]
                                .Observer(fromGroup, toGroup, rangeEnumerator.Current);
                        }
                    }
                }
            }
        }

        void AddEntities()
        {
#if DEBUG && !TRECS_IS_PROFILING
            _initTracker.ValidateAllPending();
            _initTracker.Clear();
#endif
            //current buffer becomes other, and other becomes current
            _groupedEntityToAdd.Swap();

            //I need to iterate the previous current, which is now other
            if (_groupedEntityToAdd.AnyPreviousEntityCreated())
            {
                _cachedRangeOfSubmittedIndices.Clear();
                using (TrecsProfiling.Start("Add operations"))
                {
                    try
                    {
                        using (TrecsProfiling.Start("Add entities to database"))
                        {
                            //each group is indexed by aspect type. for each type there is a dictionary indexed
                            //by entityHandle
                            foreach (var groupToSubmit in _groupedEntityToAdd)
                            {
                                if (groupToSubmit.Components.Count == 0)
                                {
                                    continue;
                                }

                                var groupId = groupToSubmit.GroupIndex;
                                var groupDB = GetDBGroup(groupId);

                                EntityRange? addedIndices = null;

                                //add the entityComponents in the group
                                foreach (var (type, fromDictionary) in groupToSubmit.Components)
                                {
                                    var toDictionary = GetOrAddTypeSafeDictionary(
                                        groupId,
                                        groupDB,
                                        type,
                                        fromDictionary
                                    );

                                    var componentAddedIndices = new EntityRange(
                                        toDictionary.Count,
                                        toDictionary.Count + fromDictionary.Count
                                    );

                                    if (addedIndices.HasValue)
                                    {
                                        Assert.That(addedIndices == componentAddedIndices);
                                    }
                                    else
                                    {
                                        addedIndices = componentAddedIndices;
                                    }

                                    //Fill the DB with the entity components generated this frame.
                                    fromDictionary.AddEntitiesToDictionary(toDictionary, groupId);
                                }

                                Assert.That(addedIndices.HasValue);

                                // Now that entities are in the DB, set up EntityHandleMap with correct DB indices
                                if (
                                    _groupedEntityToAdd.LastPendingReferences.TryGetValue(
                                        groupId,
                                        out var pendingRefs
                                    )
                                )
                                {
                                    var dbBaseIndex = addedIndices.Value.Start;
                                    for (int refIdx = 0; refIdx < pendingRefs.Count; refIdx++)
                                    {
                                        var dbIndex = dbBaseIndex + refIdx;
                                        _entitiesQuerier._entityLocator.SetEntityHandle(
                                            pendingRefs[refIdx],
                                            new EntityIndex(dbIndex, groupId)
                                        );
                                    }
                                }

                                //all the new entities are added at the end of each dictionary list, so we can
                                //just iterate the list using the indices ranges added in the _cachedIndices
                                _cachedRangeOfSubmittedIndices.Add(addedIndices.Value);
                            }
                        }

                        //then submit everything to the systems, so that the DB is up to date with all the entity components
                        //created by the entity built
                        var enumerator = _cachedRangeOfSubmittedIndices.GetEnumerator();

                        using (TrecsProfiling.Start("Add entities to systems"))
                        {
                            foreach (GroupInfo groupToSubmit in _groupedEntityToAdd)
                            {
                                if (groupToSubmit.Components.Count == 0)
                                {
                                    continue;
                                }

                                var groupId = groupToSubmit.GroupIndex;
                                var groupDB = GetDBGroup(groupId);

                                enumerator.MoveNext();

                                if (
                                    _eventsManager.ReactiveOnAddedObservers.TryGetValue(
                                        groupId,
                                        out var groupAddedObservers
                                    )
                                )
                                {
                                    for (var i = 0; i < groupAddedObservers.Count; i++)
                                    {
                                        groupAddedObservers[i]
                                            .Observer(groupId, enumerator.Current);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        using (TrecsProfiling.Start("clear double buffering"))
                        {
                            //other can be cleared now, but let's avoid deleting the dictionary every time
                            _groupedEntityToAdd.ClearLastAddOperations();
                        }
                    }
                }
            }
        }

        internal bool HasMadeNewStructuralChangesInThisIteration()
        {
            return _groupedEntityToAdd.AnyEntityCreated()
                || _entitiesOperations.AnyOperationQueued();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void RemoveEntitiesFromGroup(GroupIndex groupId)
        {
            _entitiesQuerier._entityLocator.RemoveAllGroupReferenceLocators(groupId);

            if (
                _eventsManager.ReactiveOnRemovedObservers.TryGetValue(
                    groupId,
                    out var groupRemovedObservers
                )
            )
            {
                var count = _entitiesQuerier.CountEntitiesInGroup(groupId);
                var rangeValues = new EntityRange(0, count);

                for (var i = 0; i < groupRemovedObservers.Count; i++)
                {
                    groupRemovedObservers[i].Observer(groupId, rangeValues);
                }
            }

            var dictionariesOfEntities = _componentStore.GroupEntityComponentsDB[groupId.Index];
            foreach (var dictionaryOfEntities in dictionariesOfEntities)
            {
                dictionaryOfEntities.Value.Clear();

                _componentStore.GroupsPerComponent[dictionaryOfEntities.Key][groupId].Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SwapEntitiesBetweenGroups(GroupIndex fromGroupId, GroupIndex toGroupId)
        {
            DenseDictionary<ComponentId, IComponentArray> fromGroup = GetDBGroup(fromGroupId);
            DenseDictionary<ComponentId, IComponentArray> toGroup = GetDBGroup(toGroupId);

            _entitiesQuerier._entityLocator.UpdateAllGroupReferenceLocators(fromGroupId, toGroupId);

            //remove entities from dictionaries
            foreach (var dictionaryOfEntities in fromGroup)
            {
                var refWrapperType = dictionaryOfEntities.Key;

                IComponentArray fromDictionary = dictionaryOfEntities.Value;
                IComponentArray toDictionary = GetOrAddTypeSafeDictionary(
                    toGroupId,
                    toGroup,
                    refWrapperType,
                    fromDictionary
                );

                fromDictionary.AddEntitiesToDictionary(toDictionary, toGroupId);
            }

            if (
                _eventsManager.ReactiveOnMovedObservers.TryGetValue(
                    toGroupId,
                    out var groupSwappedObservers
                )
            )
            {
                var count = _entitiesQuerier.CountEntitiesInGroup(fromGroupId);
                var rangeValues = new EntityRange(0, count);

                for (var i = 0; i < groupSwappedObservers.Count; i++)
                {
                    groupSwappedObservers[i].Observer(fromGroupId, toGroupId, rangeValues);
                }
            }

            //remove entities from dictionaries
            foreach (var dictionaryOfEntities in fromGroup)
            {
                dictionaryOfEntities.Value.Clear();

                _componentStore.GroupsPerComponent[dictionaryOfEntities.Key][fromGroupId].Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        IComponentArray GetOrAddTypeSafeDictionary(
            GroupIndex groupId,
            DenseDictionary<ComponentId, IComponentArray> groupPerComponentType,
            ComponentId typeId,
            IComponentArray fromDictionary
        )
        {
            return _componentStore.GetOrAddTypeSafeDictionary(
                groupId,
                groupPerComponentType,
                typeId,
                fromDictionary
            );
        }

        internal NativeWorldAccessor ProvideNativeWorldAccessor(
            int accessorId,
            bool canMakeStructuralChanges,
            float deltaTime,
            float elapsedTime
        )
        {
            var flags = NativeWorldAccessorFlags.None;
            if (canMakeStructuralChanges)
                flags |= NativeWorldAccessorFlags.AllowStructuralChanges;
            if (_trecsSettings.RequireDeterministicSubmission)
                flags |= NativeWorldAccessorFlags.RequireDeterministicIds;

            return new NativeWorldAccessor(
                _nativeAddOperationQueue,
                _nativeMoveOperationQueue,
                _nativeRemoveOperationQueue,
                accessorId,
                _entitiesQuerier._entityLocator,
                flags,
                _nativeSharedHeap.Resolver,
                _nativeUniqueHeap.Resolver,
                _setStore.DeferredQueues,
                deltaTime,
                elapsedTime
            );
        }

        internal NativeArray<EntityHandle> BatchClaimIds(int count, Allocator allocator)
        {
            return _entitiesQuerier._entityLocator.BatchClaimIds(count, allocator);
        }

        static TagSet DequeueTagSet(ref NativeBag buffer)
        {
            var tagCount = buffer.Dequeue<int>();

            if (tagCount == -1)
                return new TagSet(buffer.Dequeue<int>());

            switch (tagCount)
            {
                case 1:
                    return TagSet.FromTags(new Tag(buffer.Dequeue<int>()));
                case 2:
                    return TagSet.FromTags(
                        new Tag(buffer.Dequeue<int>()),
                        new Tag(buffer.Dequeue<int>())
                    );
                case 3:
                    return TagSet.FromTags(
                        new Tag(buffer.Dequeue<int>()),
                        new Tag(buffer.Dequeue<int>()),
                        new Tag(buffer.Dequeue<int>())
                    );
                case 4:
                    return TagSet.FromTags(
                        new Tag(buffer.Dequeue<int>()),
                        new Tag(buffer.Dequeue<int>()),
                        new Tag(buffer.Dequeue<int>()),
                        new Tag(buffer.Dequeue<int>())
                    );
                default:
                    throw new TrecsException(
                        $"Unexpected tag count {tagCount} in native operation queue"
                    );
            }
        }

        bool HasPendingNativeOperations()
        {
            return HasAnyNonEmpty(_nativeRemoveOperationQueue)
                || HasAnyNonEmpty(_nativeMoveOperationQueue)
                || HasAnyNonEmpty(_nativeAddOperationQueue);

            static bool HasAnyNonEmpty(AtomicNativeBags queue)
            {
                for (int i = 0; i < queue.Count; i++)
                {
                    if (!queue.GetBag(i).IsEmpty())
                        return true;
                }
                return false;
            }
        }

        void FlushNativeOperations()
        {
            if (!HasPendingNativeOperations())
                return;

            using (TrecsProfiling.Start("Native Remove Operations"))
            {
                var removeBuffersCount = _nativeRemoveOperationQueue.Count;

                var removals = new NativeList<(EntityIndex, int)>(Allocator.TempJob);

                using (TrecsProfiling.Start("NatRemove Dequeue"))
                {
                    for (int i = 0; i < removeBuffersCount; i++)
                    {
                        ref var buffer = ref _nativeRemoveOperationQueue.GetBag(i);

                        while (!buffer.IsEmpty())
                        {
#if TRECS_INTERNAL_CHECKS && DEBUG
                            var accessorId = buffer.Dequeue<int>();
#else
                            var accessorId = 0;
#endif
                            var entityHandlex = buffer.Dequeue<EntityIndex>();

                            _log.Trace("Removing entity {} (from native operation)", entityHandlex);
                            removals.Add((entityHandlex, accessorId));
                        }
                    }
                }

                if (_trecsSettings.RequireDeterministicSubmission)
                {
                    using (TrecsProfiling.Start("NatRemove Sort"))
                    {
                        removals.Sort();
                    }
                }

                using (TrecsProfiling.Start("NatRemove Queue"))
                {
#if DEBUG && (!TRECS_IS_PROFILING || TRECS_INTERNAL_CHECKS)
                    GroupIndex cachedGroup = default;
                    ResolvedTemplate cachedTemplate = null;

                    foreach (var (entityHandlex, accessorId) in removals)
                    {
                        if (entityHandlex.GroupIndex != cachedGroup)
                        {
                            cachedGroup = entityHandlex.GroupIndex;
                            cachedTemplate = _worldInfo.GetResolvedTemplateForGroup(cachedGroup);
                        }

#if TRECS_INTERNAL_CHECKS && DEBUG
                        CheckNativeOpsCanRemove(accessorId, entityHandlex.GroupIndex);
#endif
                        CheckRemoveEntityHandle(entityHandlex, cachedTemplate.DebugName);
                    }
#endif
                    _entitiesOperations.QueueNativeRemoveOperations(removals, _worldInfo);
                }

                removals.Dispose();
            }

            using (TrecsProfiling.Start("Native Swap Operations"))
            {
                var swapBuffersCount = _nativeMoveOperationQueue.Count;

                var swaps = new NativeList<(EntityIndex, GroupIndex, int)>(Allocator.TempJob);

                using (TrecsProfiling.Start("NatSwap Dequeue"))
                {
                    for (int i = 0; i < swapBuffersCount; i++)
                    {
                        ref var buffer = ref _nativeMoveOperationQueue.GetBag(i);

                        while (!buffer.IsEmpty())
                        {
#if TRECS_INTERNAL_CHECKS && DEBUG
                            var accessorId = buffer.Dequeue<int>();
#else
                            var accessorId = 0;
#endif
                            var from = buffer.Dequeue<EntityIndex>();
                            var toTagSet = DequeueTagSet(ref buffer);
                            var toGroup = _worldInfo.GetSingleGroupWithTags(toTagSet);

                            _log.Trace(
                                "Swapping entity {} from group {} to {} (from native operation)",
                                from.Index,
                                from.GroupIndex,
                                toGroup
                            );
                            swaps.Add((from, toGroup, accessorId));
                        }
                    }
                }

                if (_trecsSettings.RequireDeterministicSubmission)
                {
                    using (TrecsProfiling.Start("NatSwap Sort"))
                    {
                        swaps.Sort();
                    }
                }

                using (TrecsProfiling.Start("NatSwap Queue"))
                {
#if DEBUG && (!TRECS_IS_PROFILING || TRECS_INTERNAL_CHECKS)
                    GroupIndex cachedGroup = default;
                    ResolvedTemplate cachedTemplate = null;

                    foreach (var (fromEntityIndex, toGroup, accessorId) in swaps)
                    {
                        if (fromEntityIndex.GroupIndex != cachedGroup)
                        {
                            cachedGroup = fromEntityIndex.GroupIndex;
                            cachedTemplate = _worldInfo.GetResolvedTemplateForGroup(cachedGroup);
                        }

#if TRECS_INTERNAL_CHECKS && DEBUG
                        CheckNativeOpsCanMove(accessorId, fromEntityIndex.GroupIndex);
#endif
                        CheckMoveEntityHandle(fromEntityIndex, toGroup, cachedTemplate.DebugName);
                    }
#endif
                    _entitiesOperations.QueueNativeMoveOperations(swaps, _worldInfo);
                }

                swaps.Dispose();
            }

            //todo: it feels weird that these builds in the transient entities database while it could build directly to the final one
            using (TrecsProfiling.Start("Native Add Operations"))
            {
                var requireDeterministicAdds = _trecsSettings.RequireDeterministicSubmission;

                using (TrecsProfiling.Start("NatAdd Dequeue+Build"))
                {
                    // Cache group dict + per-component arrays to avoid redundant dictionary
                    // lookups for consecutive entities in the same group (common pattern).
                    GroupIndex cachedAddGroup = default;
                    DenseDictionary<ComponentId, IComponentArray> cachedGroupDict = null;
                    IComponentBuilder[] cachedComponents = null;
                    var cachedComponentArrays = _cachedNativeAddComponentArrays;

                    var addBuffersCount = _nativeAddOperationQueue.Count;
                    for (int i = 0; i < addBuffersCount; i++)
                    {
                        ref var buffer = ref _nativeAddOperationQueue.GetBag(i);
                        while (!buffer.IsEmpty())
                        {
                            // accessorId is always present for adds (needed for deterministic composite sort key)
                            var accessorId = buffer.Dequeue<int>();
                            var tagSet = DequeueTagSet(ref buffer);
                            var group = _worldInfo.GetSingleGroupWithTags(tagSet);
                            var reference = buffer.Dequeue<EntityHandle>();
                            var sortKey = buffer.Dequeue<uint>();
                            var componentCounts = buffer.Dequeue<uint>();

                            _log.Trace(
                                "Adding new entity to group {} (from native operation)",
                                group
                            );

#if TRECS_INTERNAL_CHECKS && DEBUG
                            CheckNativeOpsCanAdd(accessorId, group);
#endif

                            if (group != cachedAddGroup)
                            {
                                cachedAddGroup = group;
                                cachedComponents = _worldInfo
                                    .GetResolvedTemplateForGroup(group)
                                    .ComponentBuilders;

                                // Cache the group's component dictionaries
                                cachedGroupDict =
                                    _groupedEntityToAdd.currentComponentsToAddPerGroup.GetOrAdd(
                                        group,
                                        () => new DenseDictionary<ComponentId, IComponentArray>()
                                    );

                                // Cache per-component arrays (avoid GetOrAdd per entity)
                                if (
                                    cachedComponentArrays == null
                                    || cachedComponentArrays.Length < cachedComponents.Length
                                )
                                {
                                    cachedComponentArrays = new IComponentArray[
                                        cachedComponents.Length
                                    ];
                                    _cachedNativeAddComponentArrays = cachedComponentArrays;
                                }

                                for (int ci = 0; ci < cachedComponents.Length; ci++)
                                {
                                    var cb = cachedComponents[ci];
                                    cachedComponentArrays[ci] = cachedGroupDict.GetOrAdd(
                                        cb.ComponentId,
                                        (ref IComponentBuilder builder) =>
                                            builder.CreateDictionary(1),
                                        ref cb
                                    );
                                }
                            }

                            // Record native add start index for this group (first time we see it)
                            if (requireDeterministicAdds)
                            {
                                _groupedEntityToAdd.MarkNativeAddStartIfNeeded(
                                    group,
                                    cachedComponentArrays[0].Count
                                );
                            }

                            // Increment entity count for this group
                            _groupedEntityToAdd.IncrementEntityCount(group);

                            // Add prototype values to each component array (no dictionary lookups)
                            for (int ci = 0; ci < cachedComponents.Length; ci++)
                            {
                                cachedComponents[ci]
                                    .BuildEntityAndAddToList(cachedComponentArrays[ci]);
                            }

                            // Get insertion index from the first component array
                            int insertionIndex = cachedComponentArrays[0].Count - 1;

                            // Defer SetEntityHandle to submission time
                            _groupedEntityToAdd.AddPendingReference(group, reference);

                            // Track composite sort key for deterministic ordering
                            if (requireDeterministicAdds)
                            {
                                _groupedEntityToAdd.AddPendingNativeAddSortKey(
                                    group,
                                    accessorId,
                                    sortKey
                                );
                            }

                            //only called if Init is called on the initialized (there is something to init)
                            while (componentCounts > 0)
                            {
                                componentCounts--;

                                var typeId = buffer.Dequeue<ComponentId>();

                                IFiller componentBuilder = EntityComponentIdMap.GetBuilderFromId(
                                    typeId
                                );
                                //after the typeId, I expect the serialized component
                                componentBuilder.FillFromByteArray(
                                    cachedGroupDict,
                                    insertionIndex,
                                    buffer
                                );
                            }
                        }
                    }
                }

                // Sort native adds by composite sort key for determinism
                if (requireDeterministicAdds)
                {
                    using (TrecsProfiling.Start("NatAdd Sort"))
                    {
                        _groupedEntityToAdd.SortNativeAdds();
                    }
                }
            }
        }

#if !TRECS_INTERNAL_CHECKS || !DEBUG
        [Conditional("MEANINGLESS")]
#endif
        void CheckNativeOpsCanAdd(int accessorId, GroupIndex group) { }

#if !TRECS_INTERNAL_CHECKS || !DEBUG
        [Conditional("MEANINGLESS")]
#endif
        void CheckNativeOpsCanRemove(int accessorId, GroupIndex group) { }

#if !TRECS_INTERNAL_CHECKS || !DEBUG
        [Conditional("MEANINGLESS")]
#endif
        void CheckNativeOpsCanMove(int accessorId, GroupIndex group) { }

#if !DEBUG || TRECS_IS_PROFILING
        [Conditional("MEANINGLESS")]
#endif
        void InitStructuralChangeChecks()
        {
            _multipleOperationOnSameEntityChecker =
                new DenseDictionary<EntityIndex, OperationType>();
        }

        /// <summary>
        ///     Note: these checks can't cover add operations because the entity index isn't known until submission time.
        ///     Two operations on the same entity are not allowed between submissions.
        /// </summary>
#if !DEBUG || TRECS_IS_PROFILING
        [Conditional("MEANINGLESS")]
#endif
        public void CheckMoveEntityHandle(
            EntityIndex fromEntityIndex,
            GroupIndex toGroup,
            string entityDescriptorName
        )
        {
            if (
                _multipleOperationOnSameEntityChecker.TryGetValue(
                    fromEntityIndex,
                    out var fromOperationType
                )
            )
            {
                if (fromOperationType == OperationType.Remove)
                {
                    //remove supersedes swap, so this move is a no-op. The entity will be removed.
                    return;
                }

                // Duplicate move — first one wins, skip silently (matches QueueNativeMoveOperations dedup)
                return;
            }

            _multipleOperationOnSameEntityChecker.Add(fromEntityIndex, OperationType.SwapFrom);
        }

#if !DEBUG || TRECS_IS_PROFILING
        [Conditional("MEANINGLESS")]
#endif
        public void CheckRemoveEntityHandle(EntityIndex entityIndex, string entityDescriptorName)
        {
            if (
                _multipleOperationOnSameEntityChecker.TryGetValue(
                    entityIndex,
                    out var operationType
                )
            )
            {
                //remove supersedes swap and remove operations, this means remove is allowed
                //if the previous operation was swap or remove on the same submission
                _multipleOperationOnSameEntityChecker[entityIndex] = OperationType.Remove;
            }
            else
            {
                _multipleOperationOnSameEntityChecker.Add(entityIndex, OperationType.Remove);
            }
        }

#if !DEBUG || TRECS_IS_PROFILING
        [Conditional("MEANINGLESS")]
#endif
        void ClearChecksForMultipleOperationsOnTheSameEntity()
        {
            _multipleOperationOnSameEntityChecker.Recycle();
        }

        enum OperationType
        {
            Remove,
            SwapFrom,
        }
    }
}
