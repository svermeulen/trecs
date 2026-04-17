using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;
using Unity.Jobs;

namespace Trecs
{
    /// <summary>
    /// Primary API for interacting with the ECS world. Provides entity lifecycle operations,
    /// component access, queries, event subscriptions, and job scheduling.
    /// Heap allocations and pointer operations are accessed via <see cref="Heap"/>.
    /// <para>
    /// <b>Thread Safety:</b> WorldAccessor is <b>main-thread-only</b>. All methods must be called
    /// from the main thread. For job-safe operations, use <see cref="ToNative"/> to obtain a
    /// <see cref="NativeWorldAccessor"/> which provides thread-safe structural operations with
    /// deterministic ordering via sort keys.
    /// </para>
    /// </summary>
    public class WorldAccessor
    {
        static readonly TrecsLog _log = new(nameof(WorldAccessor));

        readonly World _world;
        readonly SystemRunner _systemRunner;
        readonly EcsStructuralOps _structuralOps;
        readonly EntityQuerier _entitiesDb;
        readonly WorldInfo _worldInfo;
        readonly EventsManager _eventsManager;
        readonly Rng _fixedRng;
        readonly Rng _variableRng;
        readonly EntityInputQueue _entityInputQueue;
        readonly bool _isFixedSystem;
        readonly bool _isInputSystem;
        readonly string _debugName;

        internal IComponentAccessRecorder AccessRecorder;

        public HeapAccessor Heap { get; }

        internal WorldAccessor(
            int id,
            World world,
            SystemRunner systemRunnerInfo,
            EcsHeapAllocator heapAllocator,
            EcsStructuralOps structuralOps,
            EntityQuerier entitiesDb,
            WorldInfo worldInfo,
            EventsManager eventsManager,
            Rng fixedRng,
            Rng variableRng,
            EntityInputQueue entityInputQueue,
            bool isInputSystem,
            bool isFixedSystem,
            string debugName
        )
        {
            Assert.That(!(isInputSystem && isFixedSystem));

            _world = world;
            _systemRunner = systemRunnerInfo;
            _structuralOps = structuralOps;
            _entitiesDb = entitiesDb;
            _worldInfo = worldInfo;
            _eventsManager = eventsManager;
            _fixedRng = fixedRng;
            _variableRng = variableRng;
            _entityInputQueue = entityInputQueue;
            _isInputSystem = isInputSystem;
            _isFixedSystem = isFixedSystem;
            _debugName = debugName;

            Id = id;
            Heap = new HeapAccessor(
                heapAllocator,
                systemRunnerInfo,
                isFixedSystem,
                isInputSystem,
                fixedRng,
                debugName
            );
        }

        public bool IsFixedSystem
        {
            get { return _isFixedSystem; }
        }

        public bool IsInputSystem
        {
            get { return _isInputSystem; }
        }

        public bool IsExecutingSystems
        {
            get { return _systemRunner.IsExecutingSystems; }
        }

        /// <summary>
        /// Phase-aware time step. Returns <see cref="FixedDeltaTime"/> in fixed-update systems
        /// or <see cref="VariableDeltaTime"/> in variable-update systems. Throws if called from
        /// an ambiguous context (e.g. a standalone accessor).
        /// </summary>
        public float DeltaTime
        {
            get
            {
                var phaseDefault = GetPhaseDefault();

                if (phaseDefault == PhaseDefault.Fixed)
                {
                    AssertTimeAccessAllowedInFixedPhase(nameof(DeltaTime));
                    return _systemRunner.FixedDeltaTime;
                }

                if (phaseDefault == PhaseDefault.Variable)
                {
                    return _systemRunner.VariableDeltaTime;
                }

                throw Assert.CreateException(
                    "Cannot access DeltaTime in context {} - use FixedDeltaTime or VariableDeltaTime explicitly instead",
                    DebugName
                );
            }
        }

        /// <summary>
        /// Phase-aware elapsed simulation time. Returns <see cref="FixedElapsedTime"/> in
        /// fixed-update systems or <see cref="VariableElapsedTime"/> in variable-update systems.
        /// Throws if called from an ambiguous context.
        /// </summary>
        public float ElapsedTime
        {
            get
            {
                var phaseDefault = GetPhaseDefault();

                if (phaseDefault == PhaseDefault.Fixed)
                {
                    AssertTimeAccessAllowedInFixedPhase(nameof(ElapsedTime));
                    return _systemRunner.FixedElapsedTime;
                }

                if (phaseDefault == PhaseDefault.Variable)
                {
                    return _systemRunner.VariableElapsedTime;
                }

                throw Assert.CreateException(
                    "Cannot access ElapsedTime in context {} - use FixedElapsedTime or VariableElapsedTime explicitly instead",
                    DebugName
                );
            }
        }

        /// <summary>
        /// Phase-aware deterministic random number generator. Returns <see cref="FixedRng"/>
        /// in fixed-update or <see cref="VariableRng"/> in variable-update systems. Each phase
        /// has an independent stream to preserve determinism.
        /// </summary>
        public Rng Rng
        {
            get
            {
                var phaseDefault = GetPhaseDefault();

                if (phaseDefault == PhaseDefault.Fixed)
                {
                    AssertCanMakeStructuralChanges();
                    return _fixedRng;
                }

                if (phaseDefault == PhaseDefault.Variable)
                {
                    AssertCanAccessVariableData();
                    return _variableRng;
                }

                throw Assert.CreateException(
                    "Cannot access Rng in context {} - use FixedRng or VariableRng explicitly instead",
                    DebugName
                );
            }
        }

        public int Frame
        {
            get
            {
                var phaseDefault = GetPhaseDefault();

                if (phaseDefault == PhaseDefault.Fixed)
                {
                    return _systemRunner.FixedFrame;
                }

                if (phaseDefault == PhaseDefault.Variable)
                {
                    AssertCanAccessVariableData();
                    return _systemRunner.VariableFrame;
                }

                throw Assert.CreateException(
                    "Cannot access Frame in context {} - use FixedFrame or VariableFrame explicitly instead",
                    DebugName
                );
            }
        }

        public Rng FixedRng
        {
            get
            {
                AssertCanMakeStructuralChanges();
                return _fixedRng;
            }
        }

        public Rng VariableRng
        {
            get
            {
                AssertCanAccessVariableData();

                return _variableRng;
            }
        }

        public string DebugName
        {
            get { return _debugName; }
        }

        public int Id { get; private set; }

        public WorldInfo WorldInfo
        {
            get
            {

                return _worldInfo;
            }
        }

        /// <summary>Whether there are any outstanding jobs that haven't completed yet.</summary>
        public bool HasOutstandingJobs => JobScheduler.HasOutstandingJobs;

        public EntityHandle GlobalEntityHandle
        {
            get
            {

                return _world.GlobalEntityHandle;
            }
        }

        public EntityIndex GlobalEntityIndex
        {
            get
            {

                return _worldInfo.GlobalEntityIndex;
            }
        }

        public float VariableElapsedTime
        {
            get
            {
                AssertCanAccessVariableData();

                return _systemRunner.VariableElapsedTime;
            }
        }

        public float FixedDeltaTime
        {
            get
            {
                AssertTimeAccessAllowedInFixedPhaseIfInFixedPhase(nameof(FixedDeltaTime));
                return _systemRunner.FixedDeltaTime;
            }
        }

        public float VariableDeltaTime
        {
            get
            {
                AssertCanAccessVariableData();

                return _systemRunner.VariableDeltaTime;
            }
        }

        public float FixedElapsedTime
        {
            get
            {
                AssertTimeAccessAllowedInFixedPhaseIfInFixedPhase(nameof(FixedElapsedTime));
                return _systemRunner.FixedElapsedTime;
            }
        }

        public int FixedFrame
        {
            get
            {

                return _systemRunner.FixedFrame;
            }
        }

        public int VariableFrame
        {
            get
            {
                AssertCanAccessVariableData();
                return _systemRunner.VariableFrame;
            }
        }

        public ISimpleObservable SubmissionCompletedEvent => _world.SubmissionCompletedEvent;

        public EntityEventsBuilder Events
        {
            get
            {
                AssertCanAccessEvents();
                return _eventsManager.Events(_worldInfo, this);
            }
        }

        internal World World
        {
            get { return _world; }
        }

        internal IReadOnlyList<ExecutableSystemInfo> Systems
        {
            get
            {

                return _systemRunner.Systems;
            }
        }

        internal IReadOnlyList<int> SortedFixedSystems
        {
            get
            {

                return _systemRunner.SortedFixedSystems;
            }
        }

        internal IReadOnlyList<int> SortedVariableSystems
        {
            get
            {

                return _systemRunner.SortedVariableSystems;
            }
        }

        internal IReadOnlyList<int> SortedInputSystems
        {
            get
            {

                return _systemRunner.SortedInputSystems;
            }
        }

        internal IReadOnlyList<int> SortedLateVariableSystems
        {
            get { return _systemRunner.SortedLateVariableSystems; }
        }

        internal RuntimeJobScheduler JobScheduler => _systemRunner.JobScheduler;
        internal WorldSafetyManager SafetyManager => _systemRunner.SafetyManager;
        internal bool RequireDeterministicSubmission =>
            _systemRunner.RequireDeterministicSubmission;

        bool CanMakeStructuralChanges => !_systemRunner.IsExecutingSystems || _isFixedSystem;

        /// <summary>
        /// Track an externally-scheduled job (not scheduled through Trecs).
        /// The job will be completed at the next phase boundary.
        /// Optionally chain <c>.Writes</c> / <c>.WritesGlobal</c> to declare component
        /// dependencies so other systems wait when accessing those components.
        /// </summary>
        public ExternalJobTracker TrackExternalJob(JobHandle handle)
        {
            JobScheduler.TrackJob(handle);
            return new ExternalJobTracker(JobScheduler, _worldInfo, handle);
        }

        /// <summary>
        /// Ensure it is safe to access a component on the main thread by completing outstanding
        /// jobs that read or write the given component in the given group.
        /// </summary>
        public bool SyncMainThread<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            return JobScheduler.SyncMainThread(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
        }

        /// <summary>
        /// Forces immediate processing of all deferred structural changes (adds, removes, moves).
        /// Normally submission happens automatically between system phases; call this only when
        /// you need entities to be visible before the next automatic submission point.
        /// Cannot be called during system execution.
        /// </summary>
        public void SubmitEntities()
        {
            Assert.That(
                !_systemRunner.IsExecutingSystems,
                "WorldAccessor accessor {} cannot submit entities during system execution",
                DebugName
            );
            _world.SubmitEntities();
        }

        /// <summary>
        /// Schedules moving an entity to the group identified by the given tags. The move is
        /// deferred until the next entity submission. The entity's component data is preserved
        /// for components shared between the source and destination groups.
        /// </summary>
        public void MoveTo(EntityIndex entityIndex, TagSet tags)
        {
            AssertCanMakeStructuralChanges();

            var toGroup = _worldInfo.GetSingleGroupWithTags(tags);

            _structuralOps.MoveTo(entityIndex, toGroup);
        }

        public void MoveTo<T1>(EntityIndex entityIndex)
            where T1 : struct, ITag => MoveTo(entityIndex, TagSet<T1>.Value);

        public void MoveTo<T1, T2>(EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag => MoveTo(entityIndex, TagSet<T1, T2>.Value);

        public void MoveTo<T1, T2, T3>(EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => MoveTo(entityIndex, TagSet<T1, T2, T3>.Value);

        public void MoveTo<T1, T2, T3, T4>(EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => MoveTo(entityIndex, TagSet<T1, T2, T3, T4>.Value);

        public void MoveTo(EntityHandle entityHandle, TagSet tags) =>
            MoveTo(entityHandle.ToIndex(_world), tags);

        public void MoveTo<T1>(EntityHandle entityHandle)
            where T1 : struct, ITag => MoveTo<T1>(entityHandle.ToIndex(_world));

        public void MoveTo<T1, T2>(EntityHandle entityHandle)
            where T1 : struct, ITag
            where T2 : struct, ITag => MoveTo<T1, T2>(entityHandle.ToIndex(_world));

        public void MoveTo<T1, T2, T3>(EntityHandle entityHandle)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => MoveTo<T1, T2, T3>(entityHandle.ToIndex(_world));

        public void MoveTo<T1, T2, T3, T4>(EntityHandle entityHandle)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => MoveTo<T1, T2, T3, T4>(entityHandle.ToIndex(_world));

        /// <summary>
        /// Schedules removal of an entity. The removal is deferred until the next entity submission.
        /// </summary>
        public void RemoveEntity(EntityIndex entityIndex)
        {
            AssertCanMakeStructuralChanges();

            _structuralOps.RemoveEntity(entityIndex);
        }

        public void RemoveEntitiesWithTags(TagSet tags)
        {
            AssertCanMakeStructuralChanges();

            var groups = WorldInfo.GetGroupsWithTags(tags);
            foreach (var group in groups)
            {
                var count = CountEntitiesInGroup(group);
                _structuralOps.RemoveAllEntitiesInGroup(group, count);
            }
        }

        public void RemoveEntitiesWithTags<T1>()
            where T1 : struct, ITag => RemoveEntitiesWithTags(TagSet<T1>.Value);

        public void RemoveEntitiesWithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => RemoveEntitiesWithTags(TagSet<T1, T2>.Value);

        public void RemoveEntitiesWithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => RemoveEntitiesWithTags(TagSet<T1, T2, T3>.Value);

        public void RemoveEntitiesWithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => RemoveEntitiesWithTags(TagSet<T1, T2, T3, T4>.Value);

        public void RemoveEntity(EntityHandle entityHandle)
        {
            RemoveEntity(entityHandle.ToIndex(_world));
        }

        /// <summary>
        /// NOTE: Get groups list from WorldInfo so that it is cached
        /// </summary>
        public int CountEntitiesInGroups(LocalReadOnlyFastList<Group> groups)
        {
            var totalCount = 0;

            foreach (var group in groups)
            {
                totalCount += _entitiesDb.CountEntitiesInGroup(group);
            }

            return totalCount;
        }

        public int CountEntitiesWithTags(TagSet tags)
        {
            return CountEntitiesInGroups(_worldInfo.GetGroupsWithTags(tags));
        }

        public int CountEntitiesWithTags<T1>()
            where T1 : struct, ITag => CountEntitiesWithTags(TagSet<T1>.Value);

        public int CountEntitiesWithTags<T1, T2>()
            where T1 : struct, ITag
            where T2 : struct, ITag => CountEntitiesWithTags(TagSet<T1, T2>.Value);

        public int CountEntitiesWithTags<T1, T2, T3>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => CountEntitiesWithTags(TagSet<T1, T2, T3>.Value);

        public int CountEntitiesWithTags<T1, T2, T3, T4>()
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => CountEntitiesWithTags(TagSet<T1, T2, T3, T4>.Value);

        /// <summary>
        /// Creates a new entity in the group identified by the given tags and returns an
        /// <see cref="EntityInitializer"/> for setting initial component values. The entity is
        /// not visible to queries until the next entity submission
        /// </summary>
        public EntityInitializer AddEntity(
            TagSet tags,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0
        )
        {
            AssertCanMakeStructuralChanges();

            var group = _worldInfo.GetSingleGroupWithTags(tags);

            return _structuralOps.AddEntity(group, callerFile, callerLine);
        }

        public EntityInitializer AddEntity<T1>(
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0
        )
            where T1 : struct, ITag => AddEntity(TagSet<T1>.Value, callerFile, callerLine);

        public EntityInitializer AddEntity<T1, T2>(
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0
        )
            where T1 : struct, ITag
            where T2 : struct, ITag => AddEntity(TagSet<T1, T2>.Value, callerFile, callerLine);

        public EntityInitializer AddEntity<T1, T2, T3>(
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0
        )
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => AddEntity(TagSet<T1, T2, T3>.Value, callerFile, callerLine);

        public EntityInitializer AddEntity<T1, T2, T3, T4>(
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0
        )
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag =>
            AddEntity(TagSet<T1, T2, T3, T4>.Value, callerFile, callerLine);

        public int CountEntitiesInGroup(Group group)
        {
            return _entitiesDb.CountEntitiesInGroup(group);
        }

        public int CountAllEntities()
        {
            int total = 0;
            foreach (var group in _worldInfo.AllGroups)
            {
                total += _entitiesDb.CountEntitiesInGroup(group);
            }
            return total;
        }

        public ComponentBufferAccessor<T> ComponentBuffer<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            return new ComponentBufferAccessor<T>(this, group);
        }

        public ComponentAccessor<T> Component<T>(EntityIndex entityIndex)
            where T : unmanaged, IEntityComponent
        {
            return new ComponentAccessor<T>(this, entityIndex);
        }

        public ComponentAccessor<T> Component<T>(EntityHandle entityHandle)
            where T : unmanaged, IEntityComponent
        {
            return new ComponentAccessor<T>(this, entityHandle.ToIndex(_entitiesDb));
        }

        public bool TryComponent<T>(EntityIndex entityIndex, out ComponentAccessor<T> componentRef)
            where T : unmanaged, IEntityComponent
        {
            SyncAndRecordRead<T>(entityIndex.Group);
            if (_entitiesDb.TryQueryEntitiesAndIndex<T>(entityIndex, out _, out _))
            {
                componentRef = new ComponentAccessor<T>(this, entityIndex);
                return true;
            }
            componentRef = default;
            return false;
        }

        /// <summary>
        /// Returns a <see cref="ComponentAccessor{T}"/> for the global (singleton) entity.
        /// Use this for world-wide state that doesn't belong to any specific entity type.
        /// </summary>
        public ComponentAccessor<T> GlobalComponent<T>()
            where T : unmanaged, IEntityComponent
        {
            return Component<T>(_worldInfo.GlobalEntityIndex);
        }

        public EntityAccessor Entity(EntityIndex entityIndex)
        {
            return new EntityAccessor(this, entityIndex);
        }

        public EntityAccessor Entity(EntityHandle entityHandle)
        {
            return new EntityAccessor(this, entityHandle);
        }

        public QueryBuilder Query()
        {
            return new QueryBuilder(this);
        }

        /// <summary>
        /// Pre-reserve a batch of EntityHandles on the main thread for use in parallel jobs.
        /// Must be called before job scheduling, not from within a job.
        /// Required for deterministic EntityHandle assignment when RequireDeterministicSubmission is true.
        /// Uses a two-phase algorithm: drains the recycled free list first, then bulk-allocates
        /// fresh sequential IDs. Much faster than individual ClaimId calls.
        /// </summary>
        public NativeArray<EntityHandle> ReserveEntityHandles(int count, Allocator allocator)
        {
            return _structuralOps.BatchClaimIds(count, allocator);
        }

        /// <summary>
        /// Returns a Burst-compatible <see cref="NativeWorldAccessor"/> for use in parallel jobs.
        /// Bundles entity lifecycle operations (add/remove/move), set mutations, entity
        /// reference resolution, and shared pointer resolution. The thread index is managed internally.
        /// <para>
        /// Structural operations (add/remove/move) on the returned accessor are only allowed
        /// from fixed systems. Read-only operations (entity reference resolution, shared pointer
        /// resolution) are available from any context.
        /// </para>
        /// </summary>
        public NativeWorldAccessor ToNative()
        {
            var phase = GetPhaseDefault();
            float dt;
            float et;

            if (phase == PhaseDefault.Fixed && _systemRunner.AssertNoTimeInFixedPhase)
            {
                // Burst jobs can't throw, so we poison DeltaTime/ElapsedTime with NaN
                // to make any arithmetic that reads them produce visibly broken output.
                dt = float.NaN;
                et = float.NaN;
            }
            else
            {
                dt =
                    phase == PhaseDefault.Fixed ? _systemRunner.FixedDeltaTime
                    : phase == PhaseDefault.Variable ? _systemRunner.VariableDeltaTime
                    : 0f;
                et =
                    phase == PhaseDefault.Fixed ? _systemRunner.FixedElapsedTime
                    : phase == PhaseDefault.Variable ? _systemRunner.VariableElapsedTime
                    : 0f;
            }

            return _structuralOps.GetNativeWorldAccessor(Id, CanMakeStructuralChanges, dt, et);
        }

        /// <summary>
        /// Returns a lazy-sync accessor for any entity set registered on the
        /// WorldBuilder via AddSet&lt;T&gt;(). No sync occurs at creation time —
        /// each operation triggers the appropriate sync (read or write) on demand.
        /// Safe to cache as a member field.
        /// </summary>
        public SetAccessor<T> Set<T>()
            where T : struct, IEntitySet
        {
            return new SetAccessor<T>(this);
        }

        // ── Deferred set operations (no sync needed) ──────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAdd<T>(EntityIndex entityIndex)
            where T : struct, IEntitySet
        {
            ref var queues = ref _structuralOps.GetDeferredQueues(EntitySet<T>.Value.Id);
            queues.AddQueue.GetBag(0).Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetAdd<T>(EntityHandle entityHandle)
            where T : struct, IEntitySet
        {
            SetAdd<T>(entityHandle.ToIndex(this));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRemove<T>(EntityIndex entityIndex)
            where T : struct, IEntitySet
        {
            ref var queues = ref _structuralOps.GetDeferredQueues(EntitySet<T>.Value.Id);
            queues.RemoveQueue.GetBag(0).Enqueue(entityIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRemove<T>(EntityHandle entityHandle)
            where T : struct, IEntitySet
        {
            SetRemove<T>(entityHandle.ToIndex(this));
        }

        internal void ClearSet(SetId setId)
        {
            GetSetCollection(setId).Clear();
        }

        /// <summary>
        /// Returns a read-only lookup view for any set.
        /// Outstanding job writes are synced and flushed first.
        /// Used internally by query iterators for unified set iteration.
        /// </summary>
        internal SetGroupLookup GetSetGroupLookup(SetId id)
        {
            SyncSetForRead(id);
            return new SetGroupLookup(_structuralOps.GetSet(id));
        }

        public bool EntityExists(EntityHandle entityHandle)
        {
            return entityHandle.Exists(_entitiesDb);
        }

        public EntityHandle GetEntityHandle(EntityIndex entityIndex)
        {
            return entityIndex.ToHandle(_entitiesDb);
        }

        public EntityIndex GetEntityIndex(EntityHandle entityHandle)
        {
            return entityHandle.ToIndex(_entitiesDb);
        }

        public bool TryGetEntityIndex(EntityHandle entityHandle, out EntityIndex entityIndex)
        {
            return entityHandle.TryToIndex(_entitiesDb, out entityIndex);
        }

        /// <summary>
        /// Enqueues an input component value for the next fixed-update frame. Only callable from
        /// <see cref="InputSystemAttribute"/> systems. The value is applied to the entity's
        /// <see cref="InputAttribute"/> component at the start of the next fixed-update tick.
        /// </summary>
        public void AddInput<T>(EntityHandle entityHandle, in T value)
            where T : unmanaged, IEntityComponent
        {
            AssertCanAddInputsSystem();

            _entityInputQueue.AddInput(_systemRunner.FixedFrame, entityHandle, value);
        }

        public void AddInput<T>(EntityIndex entityIndex, in T value)
            where T : unmanaged, IEntityComponent
        {
            AssertCanAddInputsSystem();

            _entityInputQueue.AddInput(
                _systemRunner.FixedFrame,
                GetEntityHandle(entityIndex),
                value
            );
        }

        internal NativeComponentBufferRead<T> GetBufferRead<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            SyncAndRecordRead<T>(group);
            AssertGroupHasComponent<T>(group);
            var safety = SafetyManager.GetReadHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
            return new NativeComponentBufferRead<T>(
                _entitiesDb.QuerySingleBuffer<T>(group),
                safety
            );
        }

        internal NativeComponentBufferWrite<T> GetBufferWrite<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            SyncAndRecord<T>(group);
            AssertGroupHasComponent<T>(group);
            var safety = SafetyManager.GetWriteHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
            return new NativeComponentBufferWrite<T>(
                _entitiesDb.QuerySingleBuffer<T>(group),
                safety
            );
        }

        [Conditional("DEBUG")]
        void AssertGroupHasComponent<T1>(Group group)
            where T1 : unmanaged, IEntityComponent
        {
            Assert.That(
                _worldInfo.GroupHasComponent<T1>(group),
                "Group {} does not contain component {} (system {}). The query that resolved this group is missing a constraint that ensures every matched group contains {}. Add .WithComponents<{}>() to the query, narrow it via tags/sets, or set MatchByComponents = true on the iteration attribute.",
                group,
                typeof(T1),
                DebugName,
                typeof(T1),
                typeof(T1)
            );
        }

        // ForJobScheduling variants — exposed to source-generated code via the
        // public extension methods in Trecs.Internal.JobGenSchedulingExtensions.
        // Skip SyncMainThread because the caller uses RuntimeJobScheduler
        // to compute dependencies and track the job handle instead.

        internal (NativeComponentBufferRead<T> buffer, int count) GetBufferReadForJobScheduling<T>(
            Group group
        )
            where T : unmanaged, IEntityComponent
        {
            AssertGroupHasComponent<T>(group);
            var (nb, count) = _entitiesDb.QuerySingleBufferWithCount<T>(group);
            var safety = SafetyManager.GetReadHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
            return (new NativeComponentBufferRead<T>(nb, safety), count);
        }

        internal (
            NativeComponentBufferWrite<T> buffer,
            int count
        ) GetBufferWriteForJobScheduling<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            AssertGroupHasComponent<T>(group);
            var (nb, count) = _entitiesDb.QuerySingleBufferWithCount<T>(group);
            var safety = SafetyManager.GetWriteHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
            return (new NativeComponentBufferWrite<T>(nb, safety), count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SyncAndRecord<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            if (_systemRunner.JobScheduler.HasOutstandingJobs)
            {
                var synced = _systemRunner.JobScheduler.SyncMainThread(
                    ResourceId.Component(ComponentTypeId<T>.Value),
                    group
                );
#if DEBUG && !TRECS_IS_PROFILING
                if (synced && _systemRunner.WarnOnJobSyncPoints)
                {
                    _log.Warning(
                        "Sync point in '{}': main-thread write access to {} in group {} completed outstanding jobs. "
                            + "Consider reordering systems to avoid this.",
                        _debugName,
                        typeof(T).Name,
                        group
                    );
                }
#endif
            }
            AccessRecorder?.OnComponentAccess(
                _debugName,
                group,
                ComponentTypeId<T>.Value,
                isReadOnly: false
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SyncAndRecordRead<T>(Group group)
            where T : unmanaged, IEntityComponent
        {
            if (_systemRunner.JobScheduler.HasOutstandingJobs)
            {
                var synced = _systemRunner.JobScheduler.SyncMainThreadForRead(
                    ResourceId.Component(ComponentTypeId<T>.Value),
                    group
                );
#if DEBUG && !TRECS_IS_PROFILING
                if (synced && _systemRunner.WarnOnJobSyncPoints)
                {
                    _log.Warning(
                        "Sync point in '{}': main-thread read access to {} in group {} completed outstanding writer job. "
                            + "Consider reordering systems to avoid this.",
                        _debugName,
                        typeof(T).Name,
                        group
                    );
                }
#endif
            }
            AccessRecorder?.OnComponentAccess(
                _debugName,
                group,
                ComponentTypeId<T>.Value,
                isReadOnly: true
            );
        }

        internal NativeComponentRead<T> GetComponentRead<T>(EntityIndex entityIndex)
            where T : unmanaged, IEntityComponent
        {
            SyncAndRecordRead<T>(entityIndex.Group);
            var buf = _entitiesDb.QueryEntitiesAndIndex<T>(entityIndex, out var index);
            var safety = SafetyManager.GetReadHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                entityIndex.Group
            );
            return new(new NativeComponentBufferRead<T>(buf, safety), (int)index);
        }

        internal NativeComponentWrite<T> GetComponentWrite<T>(EntityIndex entityIndex)
            where T : unmanaged, IEntityComponent
        {
            SyncAndRecord<T>(entityIndex.Group);
            var buf = _entitiesDb.QueryEntitiesAndIndex<T>(entityIndex, out var index);
            var safety = SafetyManager.GetWriteHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                entityIndex.Group
            );
            return new(new NativeComponentBufferWrite<T>(buf, safety), (int)index);
        }

        internal ref EntitySet GetSetDirect(SetDef setDef)
        {
            return ref _structuralOps.GetSet(setDef);
        }

        /// <summary>
        /// Returns a set reference for job scheduling (no sync).
        /// Used by generated scheduling code to avoid unnecessary sync points.
        /// </summary>
        internal ref EntitySet GetSetForJobScheduling<T>()
            where T : struct, IEntitySet
        {
            return ref _structuralOps.GetSet(EntitySet<T>.Value);
        }

        internal ref EntitySet GetSetForJobScheduling(SetId setId)
        {
            return ref _structuralOps.GetSet(setId);
        }

        /// <summary>
        /// Sync outstanding writer jobs for this set, then flush their buffered writes.
        /// Use this before reading set data on the main thread.
        /// </summary>
        internal void SyncSetForRead(SetId setId)
        {
            ref var setCollection = ref _structuralOps.GetSet(setId);

            if (_systemRunner.JobScheduler.HasOutstandingJobs)
            {
                var resourceId = ResourceId.Set(setId);
                foreach (var entry in setCollection._entriesPerGroup)
                {
                    _systemRunner.JobScheduler.SyncMainThreadForRead(resourceId, entry.Key);
                }
            }

            setCollection.FlushJobWrites();
        }

        /// <summary>
        /// Sync ALL outstanding jobs (readers and writers) for this set, then flush
        /// buffered writes. Use this before mutating set data on the main thread.
        /// </summary>
        internal void SyncSetForWrite(SetId setId)
        {
            ref var setCollection = ref _structuralOps.GetSet(setId);

            if (_systemRunner.JobScheduler.HasOutstandingJobs)
            {
                var resourceId = ResourceId.Set(setId);
                foreach (var entry in setCollection._entriesPerGroup)
                {
                    _systemRunner.JobScheduler.SyncMainThread(resourceId, entry.Key);
                }
            }

            setCollection.FlushJobWrites();
        }

        internal ref EntitySet GetSetCollection(SetId setId)
        {
            return ref _structuralOps.GetSet(setId);
        }

        internal EntityHandleMap GetEntityHandleMap()
        {
            return _entitiesDb.GetEntityHandleMap();
        }

        internal NativeEntityHandleBuffer GetEntityHandleBufferForJobScheduling(Group group)
        {
            var handleMap = GetEntityHandleMap();
            if (!handleMap._entityIndexToReferenceMap.TryGetValue(group, out var groupList))
                return default;
            return new NativeEntityHandleBuffer(new Internal.NativeBuffer<EntityHandle>(groupList));
        }

        internal NativeComponentLookupRead<T> CreateNativeComponentLookupRead<T>(
            TagSet tags,
            Allocator allocator
        )
            where T : unmanaged, IEntityComponent
        {
            return CreateNativeComponentLookupRead<T>(
                _worldInfo.GetGroupsWithTags(tags),
                allocator
            );
        }

        internal unsafe NativeComponentLookupRead<T> CreateNativeComponentLookupRead<T>(
            ReadOnlyFastList<Group> groups,
            Allocator allocator
        )
            where T : unmanaged, IEntityComponent
        {
            _entitiesDb.BuildNativeComponentLookupEntries<T>(
                groups,
                allocator,
                out var entries,
                out var count
            );
            return new NativeComponentLookupRead<T>(entries, count, allocator);
        }

        internal NativeComponentLookupWrite<T> CreateNativeComponentLookupWrite<T>(
            TagSet tags,
            Allocator allocator
        )
            where T : unmanaged, IEntityComponent
        {
            return CreateNativeComponentLookupWrite<T>(
                _worldInfo.GetGroupsWithTags(tags),
                allocator
            );
        }

        internal unsafe NativeComponentLookupWrite<T> CreateNativeComponentLookupWrite<T>(
            ReadOnlyFastList<Group> groups,
            Allocator allocator
        )
            where T : unmanaged, IEntityComponent
        {
            _entitiesDb.BuildNativeComponentLookupEntries<T>(
                groups,
                allocator,
                out var entries,
                out var count
            );
            return new NativeComponentLookupWrite<T>(entries, count, allocator);
        }

        internal bool IsSystemEnabledImpl(int i)
        {
            return _systemRunner.IsSystemEnabled(i);
        }

        internal void SetSystemEnabledImpl(int index, bool enabled)
        {
            _systemRunner.SetSystemEnabled(index, enabled);
        }

        [Conditional("DEBUG")]
        void AssertCanAddInputsSystem()
        {
            Assert.That(
                !_systemRunner.IsExecutingSystems || _isInputSystem,
                "Attempted to use input system only functionality from a non-input system {}",
                DebugName
            );
        }

        [Conditional("DEBUG")]
        void AssertCanMakeStructuralChanges()
        {
            // Note here that we allow structural changes when not executing systems since
            // this is often needed and does not break determinism
            //
            // Valid examples are:
            //
            // - Reactive event handlers (via Events.OnAdded/OnRemoved/OnMoved)
            //      * Since these are called deterministically
            // - Initialize methods
            //      * It's common to add entities before InitialEntitiesSubmitter
            //        and this also doesn't break determinism, because we only
            //        need determinism to be preserved from the starting snapshot
            //        of recordings, and recordings don't start until after initialization
            //
            // So really the only place this is not allowed is in variable systems or input systems

            Assert.That(
                CanMakeStructuralChanges,
                "Attempted to modify fixed state from a non-fixed context {}.  This is only possible before the first submission, or from inside a fixed system execute.",
                DebugName
            );
        }

        [Conditional("DEBUG")]
        void AssertCanAccessEvents()
        {
            // Don't allow subscribing to events during execute since this creates
            // unserializable state
            // Subscribes should always occur during initialize
            Assert.That(
                !_systemRunner.IsExecutingSystems,
                "Attempted to use event functionality from system execute method {}",
                DebugName
            );
        }

        [Conditional("DEBUG")]
        void AssertCanAccessVariableData()
        {
            Assert.That(
                !_systemRunner.IsExecutingSystems || !_isFixedSystem,
                "Attempted to use variable update only functionality from fixed context {}",
                DebugName
            );
        }

        void AssertTimeAccessAllowedInFixedPhase(string memberName)
        {
            Assert.That(
                !_systemRunner.AssertNoTimeInFixedPhase,
                "Cannot access {} in the fixed-update phase when WorldSettings.AssertNoTimeInFixedPhase is enabled. "
                    + "Wall-time values are not part of the deterministic simulation contract - use FixedFrame as a tick counter instead. (Context: {})",
                memberName,
                DebugName
            );
        }

        void AssertTimeAccessAllowedInFixedPhaseIfInFixedPhase(string memberName)
        {
            if (GetPhaseDefault() == PhaseDefault.Fixed)
            {
                AssertTimeAccessAllowedInFixedPhase(memberName);
            }
        }

        PhaseDefault GetPhaseDefault()
        {
            if (!_systemRunner.IsExecutingSystems)
            {
                return PhaseDefault.None;
            }

            if (_isFixedSystem)
            {
                return PhaseDefault.Fixed;
            }

            return PhaseDefault.Variable;
        }

        enum PhaseDefault
        {
            Fixed,
            Variable,
            None,
        }
    }
}

namespace Trecs.Internal // Unsupported internal APIs
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class WorldAccessorBaseExtensions
    {
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static SystemRunner GetSystemRunner(this WorldAccessor world)
        {
            return world.World.GetSystemRunner();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static EntitySubmitter GetEntitySubmitter(this WorldAccessor world)
        {
            return world.World.GetEntitySubmitter();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static World GetWorld(this WorldAccessor world)
        {
            return world.World;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static EntityQuerier GetEntityQuerier(this WorldAccessor world)
        {
            return world.World.GetEntityQuerier();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static EntityInputQueue GetEntityInputQueue(this WorldAccessor world)
        {
            return world.World.GetEntityInputQueue();
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IReadOnlyList<ExecutableSystemInfo> GetSystems(this WorldAccessor world)
        {
            return world.Systems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IReadOnlyList<int> GetSortedFixedSystems(this WorldAccessor world)
        {
            return world.SortedFixedSystems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IReadOnlyList<int> GetSortedVariableSystems(this WorldAccessor world)
        {
            return world.SortedVariableSystems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IReadOnlyList<int> GetSortedInputSystems(this WorldAccessor world)
        {
            return world.SortedInputSystems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IReadOnlyList<int> GetSortedLateVariableSystems(this WorldAccessor world)
        {
            return world.SortedLateVariableSystems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static bool IsSystemEnabled(this WorldAccessor world, int systemIndex)
        {
            return world.IsSystemEnabledImpl(systemIndex);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void SetSystemEnabled(this WorldAccessor world, int systemIndex, bool enabled)
        {
            world.SetSystemEnabledImpl(systemIndex, enabled);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static bool SyncMainThread(
            this WorldAccessor world,
            ComponentId componentId,
            Group group
        )
        {
            return world.JobScheduler.SyncMainThread(ResourceId.Component(componentId), group);
        }
    }
}
