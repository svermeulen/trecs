using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class EntitySubmitter : IDisposable
    {
        readonly TrecsLog _log;

        readonly EventsManager _eventsManager;
        bool _isDisposed;

        readonly DoubleBufferedEntitiesToAdd _groupedEntityToAdd;
        readonly EntitiesOperations _entitiesOperations;

        readonly List<EntityRange> _cachedRangeOfSubmittedIndices;
        readonly IterableDictionary<int, int> _transientEntityIDsAffectedByRemoveAtSwapBack;
        NativeList<int> _cachedSortedDescendingRemoveIndices;

        // Submission-order copy of the per-group remove indices (cleared and
        // re-filled each per-group apply pass). Native so the
        // RemoveRefsCaptureJob Burst job can iterate it without crossing the
        // managed boundary. ExecuteRemoveForGroup populates both this
        // scratch and _cachedSortedDescendingRemoveIndices from the same
        // List<int> entityHandlesToRemove in one pass.
        NativeList<int> _cachedRemoveIndicesSubmissionOrderNative;
        readonly List<IComponentArray> _cachedSrcArrays;
        readonly List<IComponentArray> _cachedDstArrays;

        // Deferred handle-free state for OnRemoved fan-out. Mirrors the data
        // swap-back's "removed entities at the tail" trick into the handle
        // map: removed entities' handle entries are temporarily relocated to
        // the [newCount, originalCount) range of each group's reverse-map
        // list, so EntityIndex.ToHandle works inside OnRemoved callbacks. The
        // real free + TrimGroupList happens after FireRemoveCallbacks
        // completes.
        // Native so the Remove Refs Burst jobs (steps a and c) can populate
        // and read it directly. The list is one-shot per submission — built
        // by step (a) in submission order, then read by step (c) and finally
        // drained by FinalizeDeferredHandleFrees after the OnRemoved fan-out.
        NativeList<DeferredHandleFreeEntry> _cachedDeferredHandleFrees;
        readonly List<(GroupIndex Group, int PostRemoveCount)> _cachedDeferredHandleTrims;

        // Per-removed-entity originalSlot → tailSlot lookup table. NativeHashMap
        // (vs the prior IterableDictionary) because step (a)'s Burst job needs
        // native data; insertion order doesn't matter here — every lookup is
        // by exact originalSlot key.
        NativeHashMap<int, int> _cachedTailSlotByOriginalSlot;

        // Cached array for fast-path AddEntity per-group component lookups, reused
        // across submissions in DrainFastAddBags to avoid per-frame allocation.
        IComponentArray[] _cachedFastAddComponentArrays;

        // Minimum entities * components to justify parallel job scheduling overhead.
        // Set heuristically: at small batch sizes the scheduling cost exceeds the
        // parallelism benefit.
        const int ParallelJobThreshold = 500;

        static readonly Action<
            IterableDictionary<GroupIndex, NativeList<MoveInfoEntry>>[],
            IterableDictionary<EntityIndex, (EntityIndex, GroupIndex)>,
            EntitySubmitter
        > _moveEntities;

        static readonly Action<List<int>[], EntitySubmitter> _removeEntities;

        static readonly Action<GroupIndex, EntitySubmitter> _removeGroup;
        static readonly Action<GroupIndex, GroupIndex, EntitySubmitter> _swapGroup;

        internal readonly ComponentStore _componentStore;
        readonly EntityQuerier _entitiesQuerier;

        IterableDictionary<EntityIndex, OperationType> _multipleOperationOnSameEntityChecker;

        internal readonly SetStore _setStore;

        readonly NativeSharedHeap _nativeSharedHeap;
        readonly InputNativeSharedHeap _inputNativeSharedHeap;
        readonly NativeHeap _nativeUniqueChunkStore;

        readonly RuntimeJobScheduler _jobScheduler;

        readonly AtomicNativeBags _nativeRemoveOperationQueue;
        readonly AtomicNativeBags _nativeMoveOperationQueue;

        // Fast-path AddEntity staging bags — per-thread × per-group raw-byte slots.
        // Written by NativeWorldAccessor.AddEntity from Burst jobs; drained by
        // DrainFastAddBags during FlushNativeOperations.
        PerGroupAddBags _perGroupAddBags;

        internal PerGroupAddBags PerGroupAddBags => _perGroupAddBags;

        // Scratch buffers reused across submissions for the FastAddFillJob inputs.
        // Allocator.Persistent so they survive across frames; Clear()'d each
        // submission rather than recreated to avoid registering a new safety
        // handle every frame.
        NativeList<FastAddFillSlotWork> _cachedFastAddSlots;
        NativeList<FastAddComponentDest> _cachedFastAddDests;
        NativeList<int> _cachedFastAddGroupDestStartIdx;

        readonly List<(int accessorId, EntityIndex from, Tag tag, bool isSet)> _managedTagOps =
            new();

        NativeList<NativeTagOp> _tagOpsScratch;
        EntityIndex[] _coalescedKeys = new EntityIndex[64];
        CoalescedEntityChange[] _coalescedValues = new CoalescedEntityChange[64];
        int _coalescedCount;

        // Per-entity attribution of additional contributors, used only when
        // _accessRecorder != null. Keyed by EntityIndex so the swap-emit loop
        // looks up extras in O(1) rather than scanning a flat side list. The
        // inner Lists are pooled across submissions via _extraContribListPool.
        readonly IterableDictionary<EntityIndex, List<int>> _pendingExtraContributors = new();
        readonly Stack<List<int>> _extraContribListPool = new();

        // Persistent NativeLists used per submit for the dequeue→queue handoff
        // in FlushNativeOperations. Cleared between uses rather than constructed
        // fresh — each NativeList construction registers an AtomicSafetyHandle
        // and allocates list metadata that adds up over high-frequency submits.
        // Allocator.Persistent because their lifetime is the EntitySubmitter's.
        NativeList<RemovalEntry> _removalsScratch;
        NativeList<SwapEntry> _swapsScratch;

        // Per-set deferred bags are now on EntitySetStorage; no centralized set queues needed.
        readonly WorldInfo _worldInfo;
        readonly WorldAccessorRegistry _accessorRegistry;
        readonly WorldSettings _trecsSettings;

        IAccessRecorder _accessRecorder;

#if DEBUG && !TRECS_IS_PROFILING
        internal readonly HashSet<GroupIndex> _groupsWithEntitiesEverAdded = new();
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

        static unsafe PerGroupAddBags CreatePerGroupAddBags(WorldInfo worldInfo)
        {
            int groupsCount = worldInfo.AllGroups.Count;
            int headerSize = sizeof(FastAddSlotHeader);
            var slotSizes = new int[groupsCount];
            var headers = worldInfo.ComponentLayouts.Headers;
            for (int g = 0; g < groupsCount; g++)
            {
                slotSizes[g] = headerSize + headers[g].TotalEntityBytes;
            }
            return PerGroupAddBags.Create(slotSizes, Allocator.Persistent);
        }

        internal EntitySubmitter(
            TrecsLog log,
            WorldInfo worldInfo,
            WorldAccessorRegistry accessorRegistry,
            EventsManager eventsManager,
            ComponentStore componentStore,
            SetStore setStore,
            WorldSettings trecsSettings,
            EntityQuerier entitiesQuerier,
            NativeSharedHeap nativeSharedHeap,
            InputNativeSharedHeap inputNativeSharedHeap,
            NativeHeap nativeUniqueChunkStore,
            RuntimeJobScheduler jobScheduler
        )
        {
            _log = log;
            _entitiesOperations = new EntitiesOperations(log, worldInfo.AllGroups.Count);

            _jobScheduler = jobScheduler;
            _nativeSharedHeap = nativeSharedHeap;
            _inputNativeSharedHeap = inputNativeSharedHeap;
            _nativeUniqueChunkStore = nativeUniqueChunkStore;
            _cachedRangeOfSubmittedIndices = new List<EntityRange>();
            _transientEntityIDsAffectedByRemoveAtSwapBack = new IterableDictionary<int, int>();
            _cachedSortedDescendingRemoveIndices = new NativeList<int>(16, Allocator.Persistent);
            _cachedRemoveIndicesSubmissionOrderNative = new NativeList<int>(
                16,
                Allocator.Persistent
            );
            _removalsScratch = new NativeList<RemovalEntry>(16, Allocator.Persistent);
            _swapsScratch = new NativeList<SwapEntry>(16, Allocator.Persistent);
            _tagOpsScratch = new NativeList<NativeTagOp>(16, Allocator.Persistent);
            _cachedSrcArrays = new List<IComponentArray>();
            _cachedDstArrays = new List<IComponentArray>();
            _cachedDeferredHandleFrees = new NativeList<DeferredHandleFreeEntry>(
                16,
                Allocator.Persistent
            );
            _cachedDeferredHandleTrims = new List<(GroupIndex, int)>();
            _cachedTailSlotByOriginalSlot = new NativeHashMap<int, int>(64, Allocator.Persistent);
            _worldInfo = worldInfo;
            _accessorRegistry = accessorRegistry;
            _trecsSettings = trecsSettings ?? new WorldSettings();

            InitStructuralChangeChecks();
            _nativeRemoveOperationQueue = AtomicNativeBags.Create(Allocator.Persistent);
            _nativeMoveOperationQueue = AtomicNativeBags.Create(Allocator.Persistent);

            _perGroupAddBags = CreatePerGroupAddBags(worldInfo);
            _cachedFastAddSlots = new NativeList<FastAddFillSlotWork>(16, Allocator.Persistent);
            _cachedFastAddDests = new NativeList<FastAddComponentDest>(16, Allocator.Persistent);
            _cachedFastAddGroupDestStartIdx = new NativeList<int>(16, Allocator.Persistent);
            // Per-set deferred bags are allocated in EntitySetStorage.
            _eventsManager = eventsManager;

            _componentStore = componentStore;
            _groupedEntityToAdd = new DoubleBufferedEntitiesToAdd(worldInfo.AllGroups.Count);
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

        internal void SetAccessRecorder(IAccessRecorder recorder)
        {
            _accessRecorder = recorder;
        }

        public void Submit()
        {
            SubmitImpl();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FlushAllSetJobWrites()
        {
            _setStore.FlushAllSetJobWrites();
        }

        void FlushAllDeferredOps()
        {
            // Deferred heap flushes apply swap-back removals to containers that
            // jobs may have been reading from via the resolver. Catch misuse
            // (flush called mid-job) loudly in DEBUG rather than producing
            // corrupted reads.
            TrecsDebugAssert.That(
                !_jobScheduler.HasOutstandingJobs,
                "FlushAllDeferredOps called while jobs are still outstanding. "
                    + "Call scheduler.CompleteAllOutstanding() first."
            );

            using (TrecsProfiling.Start("Deferred Set Operations"))
            {
                _setStore.FlushAllDeferredOps();
            }

            FlushNativeOperations();
        }

        // Observer-cascade contract (OnAdded / OnRemoved / OnMoved):
        //   - Cascading is iterative, not recursive. Each SingleSubmission()
        //     call swaps `_thisSubmissionInfo` with `_lastSubmittedInfo`
        //     (and analogously for the add buffer via _groupedEntityToAdd.Swap)
        //     before executing operations, so any structural changes queued
        //     from observer callbacks land in the new "current" buffer and
        //     get processed in the *next* iteration of this while loop —
        //     SingleSubmission never reenters itself. _isRunningSubmit catches
        //     the only remaining re-entrancy hazard (an observer that calls
        //     Submit() directly).
        //   - The cascade is bounded by MaxSubmissionIterations (default 10).
        //     Hitting the cap throws "possible circular submission detected"
        //     in DEBUG; in release the loop just exits with structural
        //     operations still pending, which will surface as visible bugs.
        //   - Cross-run determinism: each iteration walks groups in
        //     GroupIndex.FromIndex(i) order and observers per group in the
        //     priority order set up by InsertSorted, so within an iteration
        //     observer fire order is fixed. Native-queued ops are sorted at
        //     the boundary; managed ops inherit the determinism of the user
        //     code that queued them (same as ordinary system Execute).
        //   - The strict-accessor rule (WorldAccessor.AssertIsCurrentlyExecutingAccessor)
        //     short-circuits inside observer callbacks because SystemRunner
        //     resets _currentlyExecutingAccessorId to 0 before calling
        //     Submit(). Observers may therefore use a separately-
        //     created service accessor (e.g. the OnRemoved pointer-cleanup
        //     pattern in samples/10_Pointers) without tripping the assert.
        void SubmitImpl()
        {
            _eventsManager.NotifyOnSubmissionStarted();

            TrecsDebugAssert.That(
                !_isRunningSubmit,
                "A submission started while the previous one was still flushing"
            );
            _isRunningSubmit = true;

            try
            {
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
                        using (TrecsProfiling.Start("NotifyOnSubmissionCompleted"))
                        {
                            _eventsManager.NotifyOnSubmissionCompleted();
                        }
                    }
                }
            }
            finally
            {
                // Always clear the in-flight flag so an observer exception
                // (or any other mid-submission throw) does not wedge the
                // submitter for the rest of the World's lifetime via the
                // "A submission started while the previous one was still
                // flushing" assert on the next Submit() call.
                _isRunningSubmit = false;
            }
        }

        public void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            _isDisposed = true;

            using (TrecsProfiling.Start("Final Dispose"))
            {
                _componentStore.Dispose();

                _setStore.Dispose();

                _nativeRemoveOperationQueue.Dispose();
                _nativeMoveOperationQueue.Dispose();
                _perGroupAddBags.Dispose();
                if (_cachedFastAddSlots.IsCreated)
                    _cachedFastAddSlots.Dispose();
                if (_cachedFastAddDests.IsCreated)
                    _cachedFastAddDests.Dispose();
                if (_cachedFastAddGroupDestStartIdx.IsCreated)
                    _cachedFastAddGroupDestStartIdx.Dispose();
                // Per-set deferred bags are disposed by SetStore.

                _eventsManager.Dispose();

                _groupedEntityToAdd.Dispose();

                _entitiesQuerier._entityLocator.Dispose();

                _cachedSortedDescendingRemoveIndices.Dispose();
                _cachedRemoveIndicesSubmissionOrderNative.Dispose();
                _cachedDeferredHandleFrees.Dispose();
                _cachedTailSlotByOriginalSlot.Dispose();
                _removalsScratch.Dispose();
                _swapsScratch.Dispose();
                _tagOpsScratch.Dispose();
                _entitiesOperations.Dispose();

#if DEBUG && !TRECS_IS_PROFILING
                _initTracker.Clear();
#endif
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EntityInitializer AddEntity(
            GroupIndex group,
            IComponentBuilder[] componentsToBuild,
            string descriptorName,
            string callerFile = "",
            int callerLine = 0
        )
        {
#if DEBUG && !TRECS_IS_PROFILING
            _groupsWithEntitiesEverAdded.Add(group);
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
        /// Eagerly materialize buffers for a group — the DB-side component arrays,
        /// the double-buffered staging dictionaries, and the per-group id-map list.
        /// The default behavior is lazy (first AddEntity does the work); callers
        /// that know a group is about to be heavily populated can call this to
        /// avoid the first-add allocation latency. Safe post-freeze.
        /// </summary>
        internal void Preallocate(
            GroupIndex groupId,
            int size,
            IComponentBuilder[] entityComponentsToBuild
        )
        {
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
        internal IterableDictionary<TypeId, IComponentArray> GetDBGroup(GroupIndex fromIdGroupId)
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

        // Managed-side enqueue of a SetTag op. Both managed and native paths feed
        // the same per-entity coalescing pipeline in FlushNativeOperations.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueManagedSetTag(int accessorId, EntityIndex from, Tag tag)
        {
            if (_entitiesOperations.IsScheduledForRemove(from))
                return;
            _managedTagOps.Add((accessorId, from, tag, true));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void QueueManagedUnsetTag(int accessorId, EntityIndex from, Tag tag)
        {
            if (_entitiesOperations.IsScheduledForRemove(from))
                return;
            _managedTagOps.Add((accessorId, from, tag, false));
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

        static void RemoveEntities(List<int>[] removeOperations, EntitySubmitter ecsRoot)
        {
            using (TrecsProfiling.Start("remove Entities"))
            {
                ecsRoot._cachedRangeOfSubmittedIndices.Clear();
                ecsRoot._cachedDeferredHandleFrees.Clear();
                ecsRoot._cachedDeferredHandleTrims.Clear();

                try
                {
                    for (int i = 0; i < removeOperations.Length; i++)
                    {
                        var list = removeOperations[i];
                        if (list == null || list.Count == 0)
                            continue;
                        ExecuteRemoveForGroup(GroupIndex.FromIndex(i), list, ecsRoot);
                    }

                    FireRemoveCallbacks(removeOperations, ecsRoot);
                }
                finally
                {
                    // Always finalize even if an observer threw inside
                    // FireRemoveCallbacks: handles captured in (a) live in an
                    // instance-scoped list that the *next* RemoveEntities
                    // call would unconditionally Clear(), so skipping finalize
                    // here means those IDs are never returned to the free list
                    // and the forward-map entries never bump their version.
                    // That would leak one handle slot per never-finalized
                    // entity and leave any caller-resolved EntityHandle
                    // permanently Exists()==true.
                    FinalizeDeferredHandleFrees(ecsRoot);
                }
            }
        }

        static void ExecuteRemoveForGroup(
            GroupIndex fromGroup,
            List<int> entityHandlesToRemove,
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
            var submissionOrderNative = ecsRoot._cachedRemoveIndicesSubmissionOrderNative;
            sortedIndices.Clear();
            submissionOrderNative.Clear();
            for (int i = 0; i < entityHandlesToRemove.Count; i++)
            {
                var idx = entityHandlesToRemove[i];
                sortedIndices.Add(idx);
                submissionOrderNative.Add(idx);
            }

            var originalCount = GetGroupEntityCount(fromGroupDictionary);

            using (TrecsProfiling.Start("Remove Sort+Plan"))
            {
                // Burst the sort itself (NativeList<int> with a descending
                // comparer); leave the swap-back plan in managed code because
                // its output map must preserve insertion order (chain semantics
                // for scattered-removal swap-backs and the Move pipeline's
                // chain-following). IterableDictionary preserves insertion order
                // on iteration; NativeHashMap does not, so a native swap-back
                // map would have to be a NativeList<int2> with a sidecar
                // hashmap for the Move-side lookups — bigger lift than this
                // phase intends.
                new SortIntsDescendingJob { Indices = sortedIndices }.Run();

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
            //
            // Three ordered phases:
            //   (a) Clear-and-capture: zero each removed entity's original
            //       slot in the reverse-map group list and stash its (id,
            //       tailSlot) for the post-callback finalize. The tail slot
            //       mirrors where the data swap-back placed the removed
            //       entity's component data: the i-th entry in
            //       sortedDescendingIndices ends up at slot
            //       (originalCount - 1 - i). We DO NOT bump the handle's
            //       version or return it to the free list yet — that would
            //       invalidate any handle a user observer captures via
            //       ToHandle and break free-list determinism for the
            //       in-flight submission.
            //   (b) Swap-back of survivor handle entries. Same as before:
            //       moves survivor (id, slot) pairs from their pre-swap
            //       positions to their post-swap positions. The clear in (a)
            //       leaves removed-entity source slots at 0 so the existing
            //       `if (id == 0) continue;` guard in
            //       BatchUpdateIndexAfterSwapBack skips them.
            //   (c) Relocate-to-tail: write each captured (id, tailSlot)
            //       back into the group list and update _entityHandleMap[id-1]
            //       to point at the tail slot, so EntityIndex.ToHandle
            //       resolves correctly for indices in [newCount, originalCount)
            //       inside OnRemoved callbacks. After FireRemoveCallbacks
            //       returns, FinalizeDeferredHandleFrees zeroes the tail
            //       slots, bumps the version, and returns the IDs to the
            //       free list.
            //
            // TrimGroupList and ValidateGroupConsistency are deferred to the
            // finalize step for the same reason — the tail slots must remain
            // addressable until observers have finished running.
            var postRemoveCount = originalCount - numRemovals;
            using (TrecsProfiling.Start("Remove Refs"))
            {
                ref var locator = ref ecsRoot._entitiesQuerier._entityLocator;

                // Build (originalSlot → tailSlot) so we can capture in
                // entityHandlesToRemove (submission) order. This bridge
                // exists because the tail slot is determined by position
                // in sortedIndices (descending) but we deliberately walk
                // submission order in the capture step below.
                //
                // Why submission order matters: the capture order is the
                // order in which freed ids are pushed onto the
                // _entityHandleMap free list during
                // FinalizeDeferredHandleFrees. The free list is a linked
                // list embedded in the unused slots' Index fields, with
                // _nextFreeIndex pointing at the head. Both are byte-
                // serialized by WorldStateSerializer.WriteEntityHandlesMap
                // for snapshots AND for the determinism checksum used by
                // replay / desync detection. Changing the push order here
                // would shift the checksum and desync any existing
                // recording on the first multi-entity remove. The dict +
                // submission-order capture preserves the pre-spike
                // BatchRemoveEntityHandles ordering exactly.
                // (a) Clear-and-capture in submission order. The tailMap
                // populate is fused into the Burst job (RemoveRefsCaptureJob)
                // so it runs hot in cache adjacent to the per-entity capture
                // loop instead of as a separate managed pass.
                var deferredBaseCount = ecsRoot._cachedDeferredHandleFrees.Length;
                locator.BatchClearAndCaptureRemovedHandles(
                    submissionOrderNative,
                    sortedIndices,
                    originalCount,
                    fromGroup,
                    ecsRoot._cachedTailSlotByOriginalSlot,
                    ecsRoot._cachedDeferredHandleFrees
                );

                // (b) Swap-back of survivors.
                locator.BatchUpdateIndexAfterSwapBack(
                    ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack,
                    fromGroup
                );

                // (c) Relocate the captured handle entries to their tail
                // slots so OnRemoved observers can resolve them. Walks only
                // the entries we just added (per-group slice).
                locator.BatchRelocateRemovedHandlesToTail(
                    fromGroup,
                    ecsRoot._cachedDeferredHandleFrees,
                    deferredBaseCount
                );
            }

            ecsRoot._cachedDeferredHandleTrims.Add((fromGroup, postRemoveCount));
        }

        // Finalize phase for the deferred handle frees set up by
        // ExecuteRemoveForGroup. Runs after FireRemoveCallbacks so that
        // observers had a window in which ToHandle worked for the removed
        // entities. Performs the actual version bump + free-list push, zeroes
        // the tail slots, and trims each affected group's reverse-map list
        // back down to its post-remove count.
        static void FinalizeDeferredHandleFrees(EntitySubmitter ecsRoot)
        {
            ref var locator = ref ecsRoot._entitiesQuerier._entityLocator;

            var frees = ecsRoot._cachedDeferredHandleFrees;
            unsafe
            {
                var freesPtr = (DeferredHandleFreeEntry*)frees.GetUnsafeReadOnlyPtr();
                for (int i = 0; i < frees.Length; i++)
                {
                    var entry = freesPtr[i];
                    locator.FreeHandleAtTailSlot(entry.Group, entry.Id, entry.TailSlot);
                }
            }

            var trims = ecsRoot._cachedDeferredHandleTrims;
            for (int i = 0; i < trims.Count; i++)
            {
                var entry = trims[i];
                locator.TrimGroupList(entry.Group, entry.PostRemoveCount);

#if TRECS_INTERNAL_CHECKS && DEBUG
                locator.ValidateGroupConsistency(entry.Group, entry.PostRemoveCount);
#endif
            }
        }

        /// <summary>
        /// Compute the swap-back mapping for removals processed in descending index order.
        /// For each removal, the entity at the current last index swaps into the removed slot.
        /// Insertion order is preserved on iteration (consumer
        /// <see cref="Trecs.Internal.EntityHandleMap.BatchUpdateIndexAfterSwapBack"/>
        /// depends on it for chains).
        /// </summary>
        static void ComputeSwapBackPlanDescending(
            NativeList<int> sortedDescendingIndices,
            int originalCount,
            IterableDictionary<int, int> swapBackMapping
        )
        {
            var currentCount = originalCount;
            unsafe
            {
                var indicesPtr = (int*)sortedDescendingIndices.GetUnsafeReadOnlyPtr();
                for (int i = 0; i < sortedDescendingIndices.Length; i++)
                {
                    currentCount--;
                    var removedIndex = indicesPtr[i];
                    if (removedIndex != currentCount)
                    {
                        swapBackMapping[currentCount] = removedIndex;
                    }
                }
            }
        }

        static void FireRemoveCallbacks(List<int>[] removeOperations, EntitySubmitter ecsRoot)
        {
            var rangeEnumerator = ecsRoot._cachedRangeOfSubmittedIndices.GetEnumerator();

            // Why observers can read the entities they're being told were
            // just removed: during R2/R3 in ExecuteRemoveForGroup, the
            // removed entities are deliberately kept reachable in the tail
            // of each group's backing storage.
            //
            //   - Component data: the R2 Burst job swap-backs survivors
            //     down and places removed values at slots
            //     [newCount, originalCount). ComponentArray.SetCount(newCount)
            //     then shrinks only the logical count — NativeList.Length
            //     stays at originalCount, so NativeComponentBufferRead/Write
            //     views still cover those tail slots.
            //
            //   - Handles: the R3a/R3c sequence captures each removed
            //     entity's id, writes it into the matching tail slot of
            //     the reverse-map group list, and repoints the forward-map
            //     element at that tail slot — so EntityIndex.ToHandle
            //     resolves to the still-valid pre-removal handle for
            //     observers that need to do cross-entity cleanup. The real
            //     handle free (version bump + free-list push) and the
            //     matching TrimGroupList both run in
            //     FinalizeDeferredHandleFrees after this method returns.
            //
            // rangeEnumerator yields one EntityRange per group that had
            // removals, in the iteration order RemoveEntities used.
            using (TrecsProfiling.Start("Execute remove Callbacks Fast"))
            {
                for (int groupIdx = 0; groupIdx < removeOperations.Length; groupIdx++)
                {
                    var list = removeOperations[groupIdx];
                    if (list == null || list.Count == 0)
                        continue;

                    var group = GroupIndex.FromIndex(groupIdx);
                    var fromGroupDictionary = ecsRoot.GetDBGroup(group);

                    // Invariant: if a group had a non-empty remove list at R1,
                    // its component dictionary stays non-empty through R3a —
                    // entity removal updates per-component counts, not the
                    // set of component types. ExecuteRemoveForGroup's matching
                    // early-out at line ~623 means we add exactly one range to
                    // _cachedRangeOfSubmittedIndices per group reaching this
                    // point. A divergence here would silently misalign the
                    // enumerator with the wrong group's range; fail loud.
                    TrecsDebugAssert.That(
                        fromGroupDictionary.Count > 0,
                        "FireRemoveCallbacks invariant broken: group {0} has pending removes but no component types",
                        group
                    );

                    var advanced = rangeEnumerator.MoveNext();
                    TrecsDebugAssert.That(advanced);

                    if (
                        ecsRoot._eventsManager.ReactiveOnRemovedObservers.TryGetValue(
                            group,
                            out var groupRemovedSubject
                        )
                    )
                    {
                        groupRemovedSubject.Invoke(rangeEnumerator.Current);
                    }
                }
            }
        }

        /// <summary>
        /// Get the entity count for a group from any of its component arrays
        /// (all component types are parallel arrays with the same count).
        /// </summary>
        static int GetGroupEntityCount(IterableDictionary<TypeId, IComponentArray> groupDictionary)
        {
            foreach (var (_, componentArray) in groupDictionary)
            {
                return componentArray.Count;
            }

            return 0;
        }

        static void MoveEntities(
            IterableDictionary<GroupIndex, NativeList<MoveInfoEntry>>[] moveEntitiesOperations,
            IterableDictionary<EntityIndex, (EntityIndex, GroupIndex)> entitiesIdSwaps,
            EntitySubmitter ecsRoot
        )
        {
            using (TrecsProfiling.Start("Swap entities between groups"))
            {
                for (
                    int fromGroupIdx = 0;
                    fromGroupIdx < moveEntitiesOperations.Length;
                    fromGroupIdx++
                )
                {
                    var toGroupMoveInfos = moveEntitiesOperations[fromGroupIdx];
                    if (toGroupMoveInfos == null || toGroupMoveInfos.Count == 0)
                        continue;
                    var fromGroup = GroupIndex.FromIndex(fromGroupIdx);

                    ecsRoot._cachedRangeOfSubmittedIndices.Clear();

                    using (TrecsProfiling.Start("Move RecycleSwapBack"))
                    {
                        ecsRoot._transientEntityIDsAffectedByRemoveAtSwapBack.Recycle();
                    }

                    IterableDictionary<TypeId, IComponentArray> fromGroupDictionary =
                        ecsRoot.GetDBGroup(fromGroup);

                    var sourceCount = GetGroupEntityCount(fromGroupDictionary);

                    // Precompute resolved indices and execute data movement per toGroup.
                    // These are merged into a single loop because Burst jobs complete
                    // synchronously and the swap-back chain is built incrementally.
                    using (TrecsProfiling.Start("Move Precompute+Execute"))
                    {
                        foreach (var (toGroup, fromEntityToEntityIDs) in toGroupMoveInfos)
                        {
                            if (fromEntityToEntityIDs.Length == 0)
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
            NativeList<MoveInfoEntry> fromEntityToEntityIDs,
            ref int sourceCount,
            IterableDictionary<int, int> swapBackDict
        )
        {
            var count = fromEntityToEntityIDs.Length;

            unsafe
            {
                var entriesPtr = (MoveInfoEntry*)fromEntityToEntityIDs.GetUnsafePtr();
                for (int i = 0; i < count; i++)
                {
                    var originalIndex = entriesPtr[i].EntityIndex;

                    var resolvedIndex = originalIndex;
                    while (swapBackDict.TryGetValue(resolvedIndex, out var updated))
                    {
                        resolvedIndex = updated;
                    }

                    entriesPtr[i].Info.ResolvedFromIndex = resolvedIndex;

                    sourceCount--;
                    if (resolvedIndex != sourceCount)
                    {
                        swapBackDict[sourceCount] = resolvedIndex;
                    }
                }
            }
        }

        static void ExecuteMoveForToGroup(
            GroupIndex fromGroup,
            GroupIndex toGroup,
            NativeList<MoveInfoEntry> fromEntityToEntityIDs,
            IterableDictionary<TypeId, IComponentArray> fromGroupDictionary,
            EntitySubmitter ecsRoot
        )
        {
            var numEntities = fromEntityToEntityIDs.Length;
#if DEBUG && TRECS_INTERNAL_CHECKS
            TrecsDebugAssert.That(numEntities > 0, "something went wrong, no entities to swap");
#endif

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

                    TrecsDebugAssert.That(
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
                        TrecsDebugAssert.That(toGroupIndexRange.Value == componentIndexRange);
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
                unsafe
                {
                    var entriesPtr = (MoveInfoEntry*)fromEntityToEntityIDs.GetUnsafeReadOnlyPtr();
                    for (int i = 0; i < numEntities; i++)
                    {
                        resolvedFromIndices[i] = entriesPtr[i].Info.ResolvedFromIndex;
                    }
                }

                // Pre-set toIndex on MoveInfo (sequential from destBase, known before data movement)
                var destBase = toGroupIndexRange.Value.Start;
                unsafe
                {
                    var entriesPtr = (MoveInfoEntry*)fromEntityToEntityIDs.GetUnsafePtr();
                    for (int i = 0; i < numEntities; i++)
                    {
                        entriesPtr[i].Info.ToIndex = destBase + i;
                    }
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

                TrecsDebugAssert.That(toGroupIndexRange.HasValue);
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
            IterableDictionary<GroupIndex, NativeList<MoveInfoEntry>> toGroupMoveInfos,
            EntitySubmitter ecsRoot
        )
        {
            var rangeEnumerator = ecsRoot._cachedRangeOfSubmittedIndices.GetEnumerator();

            using (TrecsProfiling.Start("Execute Swap Callbacks Fast"))
            {
                foreach (var (toGroup, fromEntityToEntityIDs) in toGroupMoveInfos)
                {
                    if (fromEntityToEntityIDs.Length == 0)
                        continue;

                    var advanced = rangeEnumerator.MoveNext();
                    TrecsDebugAssert.That(advanced);

                    if (
                        ecsRoot._eventsManager.ReactiveOnMovedObservers.TryGetValue(
                            toGroup,
                            out var groupSwappedSubject
                        )
                    )
                    {
                        groupSwappedSubject.Invoke(fromGroup, rangeEnumerator.Current);
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
            // Swap double buffers: current becomes previous, previous becomes current
            _groupedEntityToAdd.Swap();

            // Iterate the previous buffer (now swapped into "other")
            if (_groupedEntityToAdd.AnyPreviousEntityCreated())
            {
                _cachedRangeOfSubmittedIndices.Clear();
                using (TrecsProfiling.Start("Add operations"))
                {
                    try
                    {
                        using (TrecsProfiling.Start("Add entities to database"))
                        {
                            foreach (var groupToSubmit in _groupedEntityToAdd)
                            {
                                if (groupToSubmit.Components.Count == 0)
                                {
                                    continue;
                                }

                                var groupId = groupToSubmit.GroupIndex;
                                var groupDB = GetDBGroup(groupId);

                                EntityRange? addedIndices = null;

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
                                        TrecsDebugAssert.That(
                                            addedIndices == componentAddedIndices
                                        );
                                    }
                                    else
                                    {
                                        addedIndices = componentAddedIndices;
                                    }

                                    fromDictionary.AddEntitiesToDictionary(toDictionary, groupId);
                                }

                                TrecsDebugAssert.That(addedIndices.HasValue);

                                // Now that entities are in the DB, set up EntityHandleMap with correct DB indices
                                if (
                                    _groupedEntityToAdd.TryGetLastPendingReferences(
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

                                _cachedRangeOfSubmittedIndices.Add(addedIndices.Value);
                            }
                        }

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

                                var advanced = enumerator.MoveNext();
                                TrecsDebugAssert.That(advanced);

                                if (
                                    _eventsManager.ReactiveOnAddedObservers.TryGetValue(
                                        groupId,
                                        out var groupAddedSubject
                                    )
                                )
                                {
                                    groupAddedSubject.Invoke(enumerator.Current);
                                }
                            }
                        }
                    }
                    finally
                    {
                        using (TrecsProfiling.Start("clear double buffering"))
                        {
                            _groupedEntityToAdd.ClearLastAddOperations();
                        }
                    }
                }
            }
        }

        // Drives the cascade loop in SubmitImpl: returns true while
        // there's still entity-level work to apply. Deferred set ops
        // (Set<T>().DeferredAdd / .DeferredRemove / .DeferredClear) are intentionally excluded here.
        // They get drained by FlushAllDeferredOps at the end of each loop
        // iteration, so a set op queued by an observer in iteration N is
        // applied at the boundary between N and N+1. The hidden invariant
        // is that no observer fires *on a set change* — if observers ever
        // need to react to set membership changes, this check must include
        // a "any set op queued" probe to keep the cascade running.
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
                    out var groupRemovedSubject
                )
            )
            {
                var count = _entitiesQuerier.CountEntitiesInGroup(groupId);
                groupRemovedSubject.Invoke(new EntityRange(0, count));
            }

            var dictionariesOfEntities = _componentStore.GroupEntityComponentsDB[groupId.Index];
            foreach (var dictionaryOfEntities in dictionariesOfEntities)
            {
                dictionaryOfEntities.Value.Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SwapEntitiesBetweenGroups(GroupIndex fromGroupId, GroupIndex toGroupId)
        {
            IterableDictionary<TypeId, IComponentArray> fromGroup = GetDBGroup(fromGroupId);
            IterableDictionary<TypeId, IComponentArray> toGroup = GetDBGroup(toGroupId);

            _entitiesQuerier._entityLocator.UpdateAllGroupReferenceLocators(fromGroupId, toGroupId);

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
                    out var groupSwappedSubject
                )
            )
            {
                var count = _entitiesQuerier.CountEntitiesInGroup(fromGroupId);
                groupSwappedSubject.Invoke(fromGroupId, new EntityRange(0, count));
            }

            foreach (var dictionaryOfEntities in fromGroup)
            {
                dictionaryOfEntities.Value.Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        IComponentArray GetOrAddTypeSafeDictionary(
            GroupIndex groupId,
            IterableDictionary<TypeId, IComponentArray> groupPerComponentType,
            TypeId typeId,
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

        internal unsafe NativeWorldAccessor ProvideNativeWorldAccessor(
            int accessorId,
            bool canMutateSimulation,
            float deltaTime,
            float elapsedTime
        )
        {
            var flags = NativeWorldAccessorFlags.None;
            if (canMutateSimulation)
                flags |= NativeWorldAccessorFlags.AllowSimulationMutation;

            // Stamp the chunk-store resolver with the same role bit so the Burst-job
            // Write paths reachable through it (TrecsList.Write, NativeUniquePtr.Write)
            // reject Variable-role callers at Open time. Reads stay unaffected.
            var chunkStoreResolver = new NativeHeapResolver(
                in _nativeUniqueChunkStore.Resolver,
                canMutateHeap: canMutateSimulation
            );

            var layouts = _worldInfo.ComponentLayouts;
            var headersPtr = (NativeTemplateLayoutHeader*)
                NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(layouts.Headers);
            var entriesPtr = (NativeComponentLayoutEntry*)
                NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(layouts.Entries);

            var fastAdd = new FastAddNativeInfo(
                _perGroupAddBags,
                _worldInfo.TagSetIdToGroupNative,
                headersPtr,
                entriesPtr,
                layouts.TypeIdToCi
            );

            return new NativeWorldAccessor(
                _nativeMoveOperationQueue,
                _nativeRemoveOperationQueue,
                accessorId,
                _entitiesQuerier._entityLocator,
                flags,
                _nativeSharedHeap.Resolver,
                _inputNativeSharedHeap.Resolver,
                chunkStoreResolver,
                _setStore.DeferredQueues,
                fastAdd,
                deltaTime,
                elapsedTime
            );
        }

        internal NativeArray<EntityHandle> BatchClaimIds(int count, Allocator allocator)
        {
            return _entitiesQuerier._entityLocator.BatchClaimIds(count, allocator);
        }

        struct CoalescedEntityChange
        {
            public TagSet FinalTagSet;
            public int PrimaryAccessorId;
        }

        void CoalesceSortedTagOps(NativeList<NativeTagOp> sortedOps)
        {
            int totalOps = sortedOps.Length;

            if (_coalescedKeys.Length < totalOps)
            {
                int newSize = Math.Max(totalOps, _coalescedKeys.Length * 2);
                _coalescedKeys = new EntityIndex[newSize];
                _coalescedValues = new CoalescedEntityChange[newSize];
            }

            bool hasRecorder = _accessRecorder != null;
            int outIdx = 0;

            unsafe
            {
                var opsPtr = (NativeTagOp*)sortedOps.GetUnsafeReadOnlyPtr();

                int i = 0;
                while (i < totalOps)
                {
                    var entityIndex = opsPtr[i].EntityIndex;
                    var template = _worldInfo.GetResolvedTemplateForGroup(entityIndex.GroupIndex);
                    var finalTagSet = _worldInfo.ToTagSet(entityIndex.GroupIndex);
                    ulong touchedDimsMask = 0UL;
                    int primaryAccessorId = opsPtr[i].AccessorId;

                    int runStart = i;
                    do
                    {
                        var op = opsPtr[i];
                        var tag = new Tag(op.TagId);
                        bool isSet = op.IsSet;

                        if (hasRecorder && i > runStart && op.AccessorId != primaryAccessorId)
                        {
                            if (!_pendingExtraContributors.TryGetValue(entityIndex, out var extras))
                            {
                                extras =
                                    _extraContribListPool.Count > 0
                                        ? _extraContribListPool.Pop()
                                        : new List<int>();
                                _pendingExtraContributors.TryAdd(entityIndex, extras, out _);
                            }
                            extras.Add(op.AccessorId);
                        }

                        if (!template.TryGetDimForTag(tag, out var dimIdx, out var dim))
                            throw TrecsDebugAssert.CreateException(
                                "Tag {0} is not part of any partition dimension on template {1}",
                                tag,
                                template.DebugName
                            );

                        ulong bit = 1UL << dimIdx;
                        if ((touchedDimsMask & bit) != 0)
                        {
#if DEBUG
                            throw TrecsDebugAssert.CreateException(
                                "Multiple SetTag/UnsetTag calls on the same partition dimension for entity in group {0} (last tag: {1}). Within one submission an entity can have at most one structural change per dimension — combine them into a single op or coordinate across systems.",
                                entityIndex.GroupIndex,
                                tag
                            );
#else
                            _log.Error(
                                "Multiple SetTag/UnsetTag calls on the same partition dimension for entity in group {0} (last tag: {1}). Within one submission an entity can have at most one structural change per dimension — combine them into a single op or coordinate across systems. In release builds the highest tag value wins.",
                                entityIndex.GroupIndex,
                                tag
                            );
                            var activeVariantConflict = template.GetActiveVariantInGroup(
                                finalTagSet,
                                dimIdx
                            );
                            int incomingValue = isSet ? tag.Value : 0;
                            if (incomingValue <= activeVariantConflict.Value)
                            {
                                i++;
                                continue;
                            }
#endif
                        }
                        touchedDimsMask |= bit;

                        var activeVariant = template.GetActiveVariantInGroup(finalTagSet, dimIdx);
                        if (isSet)
                        {
                            finalTagSet = WorldInfo.ReplaceDimensionTags(
                                finalTagSet,
                                activeVariant,
                                tag
                            );
                        }
                        else
                        {
                            if (dim.Tags.Count != 1)
                                throw TrecsDebugAssert.CreateException(
                                    "Cannot UnsetTag<{0}>: it is a variant in a multi-variant dimension on template {1}. Use SetTag to switch variants.",
                                    tag,
                                    template.DebugName
                                );
                            finalTagSet = WorldInfo.RemoveDimensionTags(finalTagSet, activeVariant);
                        }

#if DEBUG && TRECS_INTERNAL_CHECKS
                        TrecsDebugAssert.That(
                            template.IsRegisteredGroupTagSet(finalTagSet),
                            "Coalesced FinalTagSet {0} is not a registered GroupTagSet of template {1} — XOR-direct dim math produced an unregistered id",
                            finalTagSet,
                            template.DebugName
                        );
#endif
                        i++;
                    } while (i < totalOps && opsPtr[i].EntityIndex == entityIndex);

                    _coalescedKeys[outIdx] = entityIndex;
                    _coalescedValues[outIdx] = new CoalescedEntityChange
                    {
                        FinalTagSet = finalTagSet,
                        PrimaryAccessorId = primaryAccessorId,
                    };
                    outIdx++;
                }
            }

            _coalescedCount = outIdx;
        }

        bool HasPendingNativeOperations()
        {
            return HasAnyNonEmpty(_nativeRemoveOperationQueue)
                || HasAnyNonEmpty(_nativeMoveOperationQueue)
                || HasAnyFastAddSlots(_perGroupAddBags)
                || _managedTagOps.Count > 0;

            static bool HasAnyNonEmpty(AtomicNativeBags queue)
            {
                for (int i = 0; i < queue.ThreadSlotCount; i++)
                {
                    if (!queue.GetBag(i).IsEmpty)
                        return true;
                }
                return false;
            }

            static bool HasAnyFastAddSlots(PerGroupAddBags bags)
            {
                int threads = bags.ThreadSlotCount;
                int groups = bags.GroupCount;
                for (int t = 0; t < threads; t++)
                {
                    for (int g = 0; g < groups; g++)
                    {
                        if (bags.GetCell(t, g).Length > 0)
                            return true;
                    }
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
                var removeBuffersCount = _nativeRemoveOperationQueue.ThreadSlotCount;

                var removals = _removalsScratch;
                removals.Clear();

                using (TrecsProfiling.Start("NatRemove Dequeue"))
                {
                    for (int i = 0; i < removeBuffersCount; i++)
                    {
                        ref var buffer = ref _nativeRemoveOperationQueue.GetBag(i);

                        while (!buffer.IsEmpty)
                        {
                            var accessorId = buffer.Dequeue<int>();
                            var entityHandlex = buffer.Dequeue<EntityIndex>();

                            _log.Trace(
                                "Removing entity {0} (from native operation)",
                                entityHandlex
                            );
                            removals.Add(
                                new RemovalEntry
                                {
                                    EntityIndex = entityHandlex,
                                    AccessorId = accessorId,
                                }
                            );

                            if (_accessRecorder != null)
                            {
                                var accessor = _accessorRegistry.GetAccessorById(accessorId);
                                _accessRecorder.OnEntityRemoved(
                                    accessor.DebugName,
                                    entityHandlex.GroupIndex
                                );
                            }
                        }
                    }
                }

                using (TrecsProfiling.Start("NatRemove Sort"))
                {
                    if (removals.Length > 0)
                    {
                        unsafe
                        {
                            new SortRemovalsJob
                            {
                                Ptr = (long)removals.GetUnsafePtr(),
                                Count = removals.Length,
                            }.Run();
                        }
                    }
                }

                using (TrecsProfiling.Start("NatRemove Queue"))
                {
#if DEBUG && TRECS_INTERNAL_CHECKS
                    GroupIndex cachedGroup = default;
                    ResolvedTemplate cachedTemplate = null;

                    for (int ri = 0; ri < removals.Length; ri++)
                    {
                        var removal = removals[ri];
                        if (removal.EntityIndex.GroupIndex != cachedGroup)
                        {
                            cachedGroup = removal.EntityIndex.GroupIndex;
                            cachedTemplate = _worldInfo.GetResolvedTemplateForGroup(cachedGroup);
                        }

                        CheckNativeOpsCanRemove(removal.AccessorId, removal.EntityIndex.GroupIndex);
                        CheckRemoveEntityHandle(removal.EntityIndex, cachedTemplate.DebugName);
                    }
#endif
                    _entitiesOperations.QueueNativeRemoveOperations(removals, _worldInfo);
                }
            }

            using (TrecsProfiling.Start("Native Swap Operations"))
            {
                // Per-entity coalescing: multiple SetTag/UnsetTag ops on the same
                // entity merge into a single move. Same-dim conflict throws in
                // debug, highest Tag.Value wins in release; independent-dim ops
                // commute.

                using (TrecsProfiling.Start("NatSwap Dequeue"))
                {
                    var tagOps = _tagOpsScratch;
                    tagOps.Clear();

                    using (TrecsProfiling.Start("NatSwap Drain"))
                    {
                        var swapBuffersCount = _nativeMoveOperationQueue.ThreadSlotCount;
                        for (int i = 0; i < swapBuffersCount; i++)
                        {
                            ref var buffer = ref _nativeMoveOperationQueue.GetBag(i);
                            while (!buffer.IsEmpty)
                            {
                                tagOps.Add(buffer.Dequeue<NativeTagOp>());
                            }
                        }

                        int managedCount = _managedTagOps.Count;
                        for (int i = 0; i < managedCount; i++)
                        {
                            var op = _managedTagOps[i];
                            tagOps.Add(
                                new NativeTagOp
                                {
                                    AccessorId = op.accessorId,
                                    EntityIndex = op.from,
                                    TagId = op.tag.Value,
                                    IsSet = op.isSet,
                                }
                            );
                        }
                        _managedTagOps.Clear();
                    }

                    int totalOps = tagOps.Length;
                    if (totalOps == 0)
                    {
                        _coalescedCount = 0;
                    }
                    else
                    {
                        using (TrecsProfiling.Start("NatSwap Sort Ops"))
                        {
                            unsafe
                            {
                                new SortTagOpsJob
                                {
                                    Ptr = (long)tagOps.GetUnsafePtr(),
                                    Count = totalOps,
                                }.Run();
                            }
                        }

                        using (TrecsProfiling.Start("NatSwap Merge"))
                        {
                            CoalesceSortedTagOps(tagOps);
                        }
                    }
                }

                var swaps = _swapsScratch;
                swaps.Clear();

                using (TrecsProfiling.Start("NatSwap Coalesce"))
                {
                    int pendingCount = _coalescedCount;

                    TagSet cachedTagSet = TagSet.Null;
                    GroupIndex cachedToGroup = default;

                    swaps.Resize(pendingCount, NativeArrayOptions.UninitializedMemory);
                    int swapsCount = 0;
                    bool hasRecorder = _accessRecorder != null;

                    unsafe
                    {
                        var swapsPtr = (SwapEntry*)swaps.GetUnsafePtr();

                        for (int i = 0; i < pendingCount; i++)
                        {
                            var from = _coalescedKeys[i];
                            ref var p = ref _coalescedValues[i];

                            GroupIndex toGroup;
                            if (p.FinalTagSet == cachedTagSet)
                            {
                                toGroup = cachedToGroup;
                            }
                            else
                            {
                                toGroup = _worldInfo.GetSingleGroupWithTags(p.FinalTagSet);
                                cachedTagSet = p.FinalTagSet;
                                cachedToGroup = toGroup;
                            }

                            if (toGroup == from.GroupIndex)
                                continue;

                            _log.Trace(
                                "Coalesced move: entity {0} from group {1} to {2}",
                                from.Index,
                                from.GroupIndex,
                                toGroup
                            );

                            swapsPtr[swapsCount++] = new SwapEntry
                            {
                                EntityIndex = from,
                                ToGroup = toGroup,
                                AccessorId = p.PrimaryAccessorId,
                            };

                            if (!hasRecorder)
                                continue;

                            var primary = _accessorRegistry.GetAccessorById(p.PrimaryAccessorId);
                            _accessRecorder.OnEntityMoved(
                                primary.DebugName,
                                from.GroupIndex,
                                toGroup
                            );
                            if (_pendingExtraContributors.TryGetValue(from, out var extras))
                            {
                                int extrasCount = extras.Count;
                                for (int ix = 0; ix < extrasCount; ix++)
                                {
                                    var accessor = _accessorRegistry.GetAccessorById(extras[ix]);
                                    _accessRecorder.OnEntityMoved(
                                        accessor.DebugName,
                                        from.GroupIndex,
                                        toGroup
                                    );
                                }
                            }
                        }
                    }
                    swaps.Resize(swapsCount, NativeArrayOptions.UninitializedMemory);
                }

                using (TrecsProfiling.Start("NatSwap Cleanup"))
                {
                    _coalescedCount = 0;
#if DEBUG && TRECS_INTERNAL_CHECKS
                    TrecsDebugAssert.That(
                        _accessRecorder != null || _pendingExtraContributors.Count == 0,
                        "_pendingExtraContributors has {0} entries but _accessRecorder is null — extras leaked into the no-recorder path",
                        _pendingExtraContributors.Count
                    );
#endif
                    foreach (var kvp in _pendingExtraContributors)
                    {
                        kvp.Value.Clear();
                        _extraContribListPool.Push(kvp.Value);
                    }
                    _pendingExtraContributors.Recycle();
                }

                using (TrecsProfiling.Start("NatSwap Sort"))
                {
                    if (swaps.Length > 0)
                    {
                        unsafe
                        {
                            new SortSwapsJob
                            {
                                Ptr = (long)swaps.GetUnsafePtr(),
                                Count = swaps.Length,
                            }.Run();
                        }
                    }
                }

                using (TrecsProfiling.Start("NatSwap Queue"))
                {
#if DEBUG && TRECS_INTERNAL_CHECKS
                    GroupIndex cachedGroup = default;
                    ResolvedTemplate cachedTemplate = null;

                    for (int si = 0; si < swaps.Length; si++)
                    {
                        var swap = swaps[si];
                        if (swap.EntityIndex.GroupIndex != cachedGroup)
                        {
                            cachedGroup = swap.EntityIndex.GroupIndex;
                            cachedTemplate = _worldInfo.GetResolvedTemplateForGroup(cachedGroup);
                        }

                        CheckNativeOpsCanMove(
                            swap.AccessorId,
                            swap.EntityIndex.GroupIndex,
                            swap.ToGroup
                        );
                        CheckMoveEntityHandle(
                            swap.EntityIndex,
                            swap.ToGroup,
                            cachedTemplate.DebugName
                        );
                    }
#endif
                    // _removalsScratch is the same list NatRemove Queue saw —
                    // sorted by EntityIndex.CompareTo, still untouched since
                    // NatRemove Queue returned. QueueNativeMoveOperations walks
                    // it in lockstep with sortedSwaps to gate "remove
                    // supersedes swap" without a per-swap hashset probe.
                    _entitiesOperations.QueueNativeMoveOperations(
                        swaps,
                        _removalsScratch,
                        _worldInfo
                    );
                }
            }

            using (TrecsProfiling.Start("Native Add Operations"))
            {
                using (TrecsProfiling.Start("FastAdd Drain"))
                {
                    DrainFastAddBags();
                }

                // Sort native adds by composite sort key for determinism
                using (TrecsProfiling.Start("NatAdd Sort"))
                {
                    _groupedEntityToAdd.SortNativeAdds();
                }

                // Claim ids for fire-and-forget native adds (void-handle
                // overloads on NativeWorldAccessor) post-sort, so the
                // assigned ids land in deterministic sort-key order rather
                // than bag-thread arrival order. Pre-reserved handles are
                // left as-is.
                using (TrecsProfiling.Start("NatAdd Claim"))
                {
                    _groupedEntityToAdd.ClaimDeferredHandlesForNativeAdds(
                        ref _entitiesQuerier._entityLocator
                    );
                }
            }
        }

        // Drains the per-group fast-path AddEntity bags into the
        // _groupedEntityToAdd transient buffers. The shared NatAdd Sort + Claim
        // runs immediately after, applying deterministic sort-key ordering and
        // claiming handles for fire-and-forget adds.
        //
        // Slot layout per cell: [FastAddSlotHeader (48 bytes)][componentBytes per template].
        //
        // Passes:
        //   Pass 1a (managed, sequential): pre-sweep to total per-group slot counts.
        //          Lets Pass 1b resize the slot scratch list exactly once instead
        //          of per-slot Add() calls (the bounds-check cost dominated at
        //          high spike sizes).
        //   Pass 1b (managed, sequential): resize destination component arrays,
        //          build the flat FastAddFillSlotWork + FastAddComponentDest tables
        //          that drive the parallel fill job (indexed pointer writes).
        //   Pass 2  (Burst, parallel):     FastAddFillJob — one iteration per slot,
        //          decodes the slot header, branches per component on SetMask /
        //          ZeroDefaultMask, MemCpy / MemClear into the resized component
        //          arrays.
        //   Pass 3  (managed, sequential): bookkeeping — AddPendingReference,
        //          AddPendingNativeAddSortKey, IncrementEntityCount, OnEntityAdded
        //          recorder hook. Reads slot headers a second time (cells are not
        //          cleared until after this pass).
        unsafe void DrainFastAddBags()
        {
            var bags = _perGroupAddBags;
            int groupCount = bags.GroupCount;
            int threadCount = bags.ThreadSlotCount;
            var layouts = _worldInfo.ComponentLayouts;

            int headerSize = sizeof(FastAddSlotHeader);
            bool hasRecorder = _accessRecorder != null;

            using (TrecsProfiling.Start("FastAdd Setup"))
            {
                int* slotsPerGroup = stackalloc int[groupCount];
                int totalSlots = 0;
                for (int gi = 0; gi < groupCount; gi++)
                {
                    int slotSize = bags.SlotSize(gi);
                    int totalSlotsForGroup = 0;
                    for (int t = 0; t < threadCount; t++)
                    {
                        totalSlotsForGroup += bags.GetCell(t, gi).Length / slotSize;
                    }
                    slotsPerGroup[gi] = totalSlotsForGroup;
                    totalSlots += totalSlotsForGroup;
                }

                _cachedFastAddSlots.Clear();
                _cachedFastAddDests.Clear();
                _cachedFastAddGroupDestStartIdx.Clear();
                _cachedFastAddGroupDestStartIdx.Resize(
                    groupCount,
                    NativeArrayOptions.UninitializedMemory
                );
                if (totalSlots == 0)
                {
                    bags.Clear();
                    return;
                }
                _cachedFastAddSlots.Resize(totalSlots, NativeArrayOptions.UninitializedMemory);

                var slotsBase = (FastAddFillSlotWork*)_cachedFastAddSlots.GetUnsafePtr();
                int slotWriteIdx = 0;

                for (int gi = 0; gi < groupCount; gi++)
                {
                    int totalSlotsForGroup = slotsPerGroup[gi];
                    _cachedFastAddGroupDestStartIdx[gi] = _cachedFastAddDests.Length;
                    if (totalSlotsForGroup == 0)
                        continue;

                    int slotSize = bags.SlotSize(gi);
                    var group = GroupIndex.FromIndex(gi);
                    var resolvedTemplate = _worldInfo.GetResolvedTemplateForGroup(group);
                    var components = resolvedTemplate.ComponentBuilders;

                    var groupDict = _groupedEntityToAdd.GetOrCreateCurrentComponentsForGroup(group);
                    var componentArrays = _cachedFastAddComponentArrays;
                    if (componentArrays == null || componentArrays.Length < components.Length)
                    {
                        componentArrays = new IComponentArray[components.Length];
                        _cachedFastAddComponentArrays = componentArrays;
                    }
                    for (int ci = 0; ci < components.Length; ci++)
                    {
                        var cb = components[ci];
                        componentArrays[ci] = groupDict.GetOrAdd(
                            cb.TypeId,
                            (ref IComponentBuilder builder) => builder.CreateDictionary(1),
                            ref cb
                        );
                    }

                    int baseIndex = componentArrays[0].Count;
                    _groupedEntityToAdd.MarkNativeAddStartIfNeeded(group, baseIndex);

                    int newCount = baseIndex + totalSlotsForGroup;
                    for (int ci = 0; ci < components.Length; ci++)
                    {
                        componentArrays[ci].EnsureCapacity(newCount);
                        componentArrays[ci].SetCount(newCount);
                        _cachedFastAddDests.Add(
                            new FastAddComponentDest
                            {
                                ArrayPtr = (long)componentArrays[ci].GetUnsafePtr(),
                                ElementSize = componentArrays[ci].ElementSize,
                            }
                        );
                    }

                    int destOffsetInGroup = 0;
                    for (int t = 0; t < threadCount; t++)
                    {
                        int cellLen = bags.GetCell(t, gi).Length;
                        int slotCount = cellLen / slotSize;
                        if (slotCount == 0)
                            continue;
                        byte* basePtr = bags.GetCell(t, gi).Ptr;
                        for (int s = 0; s < slotCount; s++)
                        {
                            slotsBase[slotWriteIdx++] = new FastAddFillSlotWork
                            {
                                SlotPtr = (long)(basePtr + s * slotSize),
                                DestIdx = baseIndex + destOffsetInGroup,
                                GroupIdx = gi,
                            };
                            destOffsetInGroup++;
                        }
                    }
                }

                TrecsDebugAssert.That(slotWriteIdx == totalSlots);
            }

            using (TrecsProfiling.Start("FastAdd Fill"))
            {
                var job = new FastAddFillJob
                {
                    Slots = _cachedFastAddSlots.AsArray(),
                    LayoutHeaders = layouts.Headers,
                    LayoutEntries = layouts.Entries,
                    ComponentDests = _cachedFastAddDests.AsArray(),
                    GroupComponentDestStartIdx = _cachedFastAddGroupDestStartIdx.AsArray(),
                    DefaultBytesBase = (long)layouts.DefaultBytes.GetUnsafeReadOnlyPtr(),
                    HeaderSize = headerSize,
                };
                const int batchSize = 256;
                job.Schedule(_cachedFastAddSlots.Length, batchSize).Complete();
            }

            using (TrecsProfiling.Start("FastAdd Bookkeeping"))
            {
                int totalSlots = _cachedFastAddSlots.Length;
                for (int i = 0; i < totalSlots; i++)
                {
                    var work = _cachedFastAddSlots[i];
                    var hdr = (FastAddSlotHeader*)work.SlotPtr;
                    var group = GroupIndex.FromIndex(work.GroupIdx);
                    int accessorId = hdr->AccessorId;

#if TRECS_INTERNAL_CHECKS && DEBUG
                    CheckNativeOpsCanAdd(accessorId, group);
#endif
                    if (hasRecorder)
                    {
                        var accessor = _accessorRegistry.GetAccessorById(accessorId);
                        _accessRecorder.OnEntityAdded(accessor.DebugName, group);
                    }

                    _groupedEntityToAdd.IncrementEntityCount(group);
                    _groupedEntityToAdd.AddPendingReference(group, hdr->ReservedRef);
                    _groupedEntityToAdd.AddPendingNativeAddSortKey(group, accessorId, hdr->SortKey);
                }
            }

            bags.Clear();
        }

        // Native structural-change checks fire at submission time, after each op
        // is popped from its native queue. The originating accessor's id is
        // preserved through the queue (used for the deterministic composite
        // sort key on adds), so we can route the per-group VUO check through
        // the same AssertCanMakeStructuralChangesToGroup that gates the
        // main-thread paths. The currently-executing-accessor guard inside
        // that helper is a no-op during submission (the system has already
        // finished executing by the time we drain the queues).
#if !TRECS_INTERNAL_CHECKS || !DEBUG
        [Conditional("MEANINGLESS")]
#endif
        void CheckNativeOpsCanAdd(int accessorId, GroupIndex group)
        {
            _accessorRegistry
                .GetAccessorById(accessorId)
                .AssertCanMakeStructuralChangesToGroup(group);
        }

#if !TRECS_INTERNAL_CHECKS || !DEBUG
        [Conditional("MEANINGLESS")]
#endif
        void CheckNativeOpsCanRemove(int accessorId, GroupIndex group)
        {
            _accessorRegistry
                .GetAccessorById(accessorId)
                .AssertCanMakeStructuralChangesToGroup(group);
        }

#if !TRECS_INTERNAL_CHECKS || !DEBUG
        [Conditional("MEANINGLESS")]
#endif
        void CheckNativeOpsCanMove(int accessorId, GroupIndex fromGroup, GroupIndex toGroup)
        {
            // Both source and destination must be allowed for the role,
            // since a Fixed→VUO move would leak sim state into render-cadence territory.
            var accessor = _accessorRegistry.GetAccessorById(accessorId);
            accessor.AssertCanMakeStructuralChangesToGroup(fromGroup);
            accessor.AssertCanMakeStructuralChangesToGroup(toGroup);
        }

#if !DEBUG || TRECS_IS_PROFILING
        [Conditional("MEANINGLESS")]
#endif
        void InitStructuralChangeChecks()
        {
            _multipleOperationOnSameEntityChecker =
                new IterableDictionary<EntityIndex, OperationType>();
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
                    // Remove supersedes swap — this move is a no-op
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
                // Remove supersedes both swap and prior remove ops
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
