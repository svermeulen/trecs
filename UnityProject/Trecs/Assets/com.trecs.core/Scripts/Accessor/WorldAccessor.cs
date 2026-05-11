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
    public sealed class WorldAccessor
    {
        static readonly TrecsLog _log = new(nameof(WorldAccessor));

        readonly World _world;
        readonly SystemRunner _systemRunner;
        readonly SystemEnableState _systemEnableState;
        readonly EcsStructuralOps _structuralOps;
        readonly EntityQuerier _entitiesDb;
        readonly WorldInfo _worldInfo;
        readonly EventsManager _eventsManager;
        readonly Rng _fixedRng;
        readonly Rng _variableRng;
        readonly EntityInputQueue _entityInputQueue;
        readonly AccessorRole _role;
        readonly bool _isInput;
        readonly string _debugName;
        readonly string _createdAtFile;
        readonly int _createdAtLine;

        internal IAccessRecorder AccessRecorder;

        public HeapAccessor Heap { get; }

        internal WorldAccessor(
            int id,
            World world,
            SystemRunner systemRunnerInfo,
            SystemEnableState systemEnableState,
            EcsHeapAllocator heapAllocator,
            EcsStructuralOps structuralOps,
            EntityQuerier entitiesDb,
            WorldInfo worldInfo,
            EventsManager eventsManager,
            Rng fixedRng,
            Rng variableRng,
            EntityInputQueue entityInputQueue,
            AccessorRole role,
            bool isInput,
            string debugName,
            string createdAtFile,
            int createdAtLine
        )
        {
            _world = world;
            _systemRunner = systemRunnerInfo;
            _systemEnableState = systemEnableState;
            _structuralOps = structuralOps;
            _entitiesDb = entitiesDb;
            _worldInfo = worldInfo;
            _eventsManager = eventsManager;
            _fixedRng = fixedRng;
            _variableRng = variableRng;
            _entityInputQueue = entityInputQueue;
            _role = role;
            _isInput = isInput;
            _debugName = debugName;
            _createdAtFile = createdAtFile ?? string.Empty;
            _createdAtLine = createdAtLine;

            Id = id;
            Heap = new HeapAccessor(
                heapAllocator,
                systemRunnerInfo,
                role,
                isInput,
                fixedRng,
                debugName
            );
        }

        /// <summary>
        /// Source file path of the <c>CreateAccessor</c> call that produced
        /// this accessor, captured via <see cref="CallerFilePathAttribute"/>
        /// in DEBUG builds. Empty in release builds and for accessors made
        /// from non-source-tracked callsites (system-owned accessors take
        /// their identity from the system type instead). Editor tooling
        /// surfaces this so users can jump back to where a manual accessor
        /// was created.
        /// </summary>
        public string CreatedAtFile
        {
            get { return _createdAtFile; }
        }

        /// <summary>
        /// Source line of the <c>CreateAccessor</c> callsite. Pairs with
        /// <see cref="CreatedAtFile"/>; <c>0</c> when unavailable.
        /// </summary>
        public int CreatedAtLine
        {
            get { return _createdAtLine; }
        }

        /// <summary>
        /// The <see cref="AccessorRole"/> this accessor was created with — controls
        /// component read/write rules, structural-change rules, and heap-allocation
        /// rules. See <see cref="AccessorRole"/> for the full matrix.
        /// </summary>
        public AccessorRole Role
        {
            get { return _role; }
        }

        bool IsFixed => _role == AccessorRole.Fixed;
        bool IsUnrestricted => _role == AccessorRole.Unrestricted;
        bool IsInput => _isInput;

        public bool IsExecutingSystems
        {
            get { return _systemRunner.IsExecutingSystems; }
        }

        /// <summary>
        /// When true, all updates (fixed, variable, input, presentation) are skipped.
        /// Distinct from <see cref="FixedIsPaused"/>, which only halts the fixed-update phase.
        /// </summary>
        public bool IsPaused
        {
            get { return _systemRunner.IsPaused; }
            set { _systemRunner.IsPaused = value; }
        }

        /// <summary>
        /// When true, the fixed-update phase is halted; variable, input, and presentation
        /// updates continue. Changes fire <see cref="FixedIsPausedChangedEvent"/>.
        /// </summary>
        public bool FixedIsPaused
        {
            get { return _systemRunner.FixedIsPaused; }
            set { _systemRunner.FixedIsPaused = value; }
        }

        public ISimpleObservable<bool> FixedIsPausedChangedEvent =>
            _systemRunner.FixedIsPausedChangedEvent;

        /// <summary>
        /// Schedule one fixed-update frame to run on the next host tick, then
        /// resume the paused state. Requires <see cref="FixedIsPaused"/> to be
        /// true. Only fixed frames are steppable — variable frames are driven
        /// by the host update loop.
        /// </summary>
        public void StepFixedFrame()
        {
            _systemRunner.StepFixedFrame();
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

        public int Id { get; }

        public WorldInfo WorldInfo => _worldInfo;

        /// <summary>Whether there are any outstanding jobs that haven't completed yet.</summary>
        public bool HasOutstandingJobs => JobScheduler.HasOutstandingJobs;

        public EntityHandle GlobalEntityHandle => _world.GlobalEntityHandle;

        internal EntityIndex GlobalEntityIndex => _worldInfo.GlobalEntityIndex;

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

        public int FixedFrame => _systemRunner.FixedFrame;

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

        internal IReadOnlyList<ExecutableSystemInfo> Systems => _systemRunner.Systems;

        internal IReadOnlyList<int> SortedFixedSystems => _systemRunner.SortedFixedSystems;

        internal IReadOnlyList<int> SortedInputSystems
        {
            get { return _systemRunner.SortedInputSystems; }
        }

        internal IReadOnlyList<int> SortedEarlyPresentationSystems
        {
            get { return _systemRunner.SortedEarlyPresentationSystems; }
        }

        internal IReadOnlyList<int> SortedPresentationSystems
        {
            get { return _systemRunner.SortedPresentationSystems; }
        }

        internal IReadOnlyList<int> SortedLatePresentationSystems
        {
            get { return _systemRunner.SortedLatePresentationSystems; }
        }

        internal RuntimeJobScheduler JobScheduler => _systemRunner.JobScheduler;
        internal WorldSafetyManager SafetyManager => _systemRunner.SafetyManager;
        internal bool RequireDeterministicSubmission =>
            _systemRunner.RequireDeterministicSubmission;

        bool CanMakeStructuralChanges => IsUnrestricted || IsFixed;

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
        public bool SyncMainThread<T>(GroupIndex group)
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
        internal void MoveTo(EntityIndex entityIndex, TagSet tags)
        {
            // MoveTo touches both source and destination groups; both must
            // be allowed for the role. The dest's role-allowance dictates
            // whether VUO templates can receive moves from this accessor.
            AssertCanMakeStructuralChangesToGroup(entityIndex.GroupIndex);

            var toGroup = _worldInfo.GetSingleGroupWithTags(tags);

            AssertCanMakeStructuralChangesToGroup(toGroup);

            // Same-group moves would re-add the entity at a new slot rather
            // than no-op — short-circuit them. Matters in particular for
            // SetTag/UnsetTag where the destination can already equal the
            // source partition (e.g. SetTag<T> on an entity already tagged
            // with T).
            if (toGroup == entityIndex.GroupIndex)
                return;

            _structuralOps.MoveTo(entityIndex, toGroup);

            AccessRecorder?.OnEntityMoved(_debugName, entityIndex.GroupIndex, toGroup);
        }

        internal void MoveTo<T1>(EntityIndex entityIndex)
            where T1 : struct, ITag => MoveTo(entityIndex, TagSet<T1>.Value);

        internal void MoveTo<T1, T2>(EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag => MoveTo(entityIndex, TagSet<T1, T2>.Value);

        internal void MoveTo<T1, T2, T3>(EntityIndex entityIndex)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => MoveTo(entityIndex, TagSet<T1, T2, T3>.Value);

        internal void MoveTo<T1, T2, T3, T4>(EntityIndex entityIndex)
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
        /// Schedules moving the entity to the partition where the dimension containing
        /// <typeparamref name="T"/> now has <typeparamref name="T"/> as its active
        /// variant. All other dimensions and tags are preserved. For presence/absence
        /// dimensions this sets the tag present; for multi-variant dimensions it
        /// switches the active variant. Throws if <typeparamref name="T"/> is not
        /// declared as a partition variant on the entity's template (via
        /// <see cref="IPartitionedBy{T1}"/> / <see cref="IPartitionedBy{T1, T2}"/>).
        /// </summary>
        internal void SetTag<T>(EntityIndex entityIndex)
            where T : struct, ITag
        {
            var newTagSet = _worldInfo.ResolveSetTagDestination(
                entityIndex.GroupIndex,
                Tag<T>.Value
            );
            // Skip the move when the entity is already in the destination — MoveTo
            // does not de-dupe a same-group move and would re-add the entity at
            // a new slot.
            if (newTagSet == _worldInfo.ToTagSet(entityIndex.GroupIndex))
                return;
            MoveTo(entityIndex, newTagSet);
        }

        public void SetTag<T>(EntityHandle entityHandle)
            where T : struct, ITag => SetTag<T>(entityHandle.ToIndex(_world));

        /// <summary>
        /// Schedules a tag-remove: moves the entity to the partition where
        /// <typeparamref name="T"/> is absent. Only valid when
        /// <typeparamref name="T"/> is in a presence/absence partition dimension
        /// (declared via <see cref="IPartitionedBy{T1}"/>). For multi-variant
        /// dimensions there is no "absent" partition — use
        /// <see cref="SetTag{T}(EntityIndex)"/> to switch variants instead.
        /// </summary>
        internal void UnsetTag<T>(EntityIndex entityIndex)
            where T : struct, ITag
        {
            var newTagSet = _worldInfo.ResolveUnsetTagDestination(
                entityIndex.GroupIndex,
                Tag<T>.Value
            );
            if (newTagSet == _worldInfo.ToTagSet(entityIndex.GroupIndex))
                return;
            MoveTo(entityIndex, newTagSet);
        }

        public void UnsetTag<T>(EntityHandle entityHandle)
            where T : struct, ITag => UnsetTag<T>(entityHandle.ToIndex(_world));

        /// <summary>
        /// Schedules removal of an entity. The removal is deferred until the next entity submission.
        /// </summary>
        internal void RemoveEntity(EntityIndex entityIndex)
        {
            AssertCanMakeStructuralChangesToGroup(entityIndex.GroupIndex);

            _structuralOps.RemoveEntity(entityIndex);

            AccessRecorder?.OnEntityRemoved(_debugName, entityIndex.GroupIndex);
        }

        public void RemoveEntitiesWithTags(TagSet tags)
        {
            var groups = WorldInfo.GetGroupsWithTags(tags);
            foreach (var group in groups)
            {
                AssertCanMakeStructuralChangesToGroup(group);
                var count = CountEntitiesInGroup(group);
                _structuralOps.RemoveAllEntitiesInGroup(group, count);
                AccessRecorder?.OnEntityRemoved(_debugName, group);
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
        public int CountEntitiesInGroups(LocalReadOnlyFastList<GroupIndex> groups)
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
            var group = _worldInfo.GetSingleGroupWithTags(tags);

            AssertCanMakeStructuralChangesToGroup(group);

            var initializer = _structuralOps.AddEntity(group, callerFile, callerLine);

            AccessRecorder?.OnEntityAdded(_debugName, group);

            return initializer;
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

        public int CountEntitiesInGroup(GroupIndex group)
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

        // ── Warmup ────────────────────────────────────────────────────
        // Per-group component-array slots are normally lazy: the first
        // AddEntity into a group triggers the IComponentArray allocations,
        // which keeps world startup cheap even when templates declare many
        // partitions. The Warmup* methods let callers opt back into the
        // eager behavior — either world-wide or for specific hot groups —
        // when they'd rather pay the allocation cost up front than at
        // first-add time.

        /// <summary>
        /// Eagerly materializes the component-array slots for <paramref name="group"/>
        /// and grows each to hold at least <paramref name="initialCapacity"/> entities.
        /// Safe to call multiple times; capacities only grow.
        /// </summary>
        public void Warmup(GroupIndex group, int initialCapacity = 1)
        {
            Assert.That(!group.IsNull, "Cannot warm up null group");
            Assert.That(initialCapacity >= 0, "initialCapacity must be non-negative");
            var template = _worldInfo.GetResolvedTemplateForGroup(group);
            _structuralOps.WarmupGroup(group, initialCapacity, template.ComponentBuilders);
        }

        /// <summary>
        /// Warms up the single group identified by <paramref name="tags"/>.
        /// </summary>
        public void Warmup(TagSet tags, int initialCapacity = 1) =>
            Warmup(_worldInfo.GetSingleGroupWithTags(tags), initialCapacity);

        public void Warmup<T1>(int initialCapacity = 1)
            where T1 : struct, ITag => Warmup(TagSet<T1>.Value, initialCapacity);

        public void Warmup<T1, T2>(int initialCapacity = 1)
            where T1 : struct, ITag
            where T2 : struct, ITag => Warmup(TagSet<T1, T2>.Value, initialCapacity);

        public void Warmup<T1, T2, T3>(int initialCapacity = 1)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag => Warmup(TagSet<T1, T2, T3>.Value, initialCapacity);

        public void Warmup<T1, T2, T3, T4>(int initialCapacity = 1)
            where T1 : struct, ITag
            where T2 : struct, ITag
            where T3 : struct, ITag
            where T4 : struct, ITag => Warmup(TagSet<T1, T2, T3, T4>.Value, initialCapacity);

        /// <summary>
        /// Warms up every group in the world — matches the pre-0.x eager-allocation
        /// behavior. Call this from setup code if you'd rather pay the allocation
        /// cost up front than at first-entity time.
        /// </summary>
        public void WarmupAllGroups(int initialCapacityPerGroup = 1)
        {
            foreach (var group in _worldInfo.AllGroups)
            {
                Warmup(group, initialCapacityPerGroup);
            }
        }

        public ComponentBufferAccessor<T> ComponentBuffer<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            return new ComponentBufferAccessor<T>(this, group);
        }

        internal ComponentAccessor<T> Component<T>(EntityIndex entityIndex)
            where T : unmanaged, IEntityComponent
        {
            return new ComponentAccessor<T>(this, entityIndex);
        }

        public ComponentAccessor<T> Component<T>(EntityHandle entityHandle)
            where T : unmanaged, IEntityComponent
        {
            return new ComponentAccessor<T>(this, entityHandle.ToIndex(_entitiesDb));
        }

        internal bool TryComponent<T>(
            EntityIndex entityIndex,
            out ComponentAccessor<T> componentRef
        )
            where T : unmanaged, IEntityComponent
        {
            SyncAndRecordRead<T>(entityIndex.GroupIndex);
            if (_entitiesDb.TryQueryEntitiesAndIndex<T>(entityIndex, out _, out _))
            {
                componentRef = new ComponentAccessor<T>(this, entityIndex);
                return true;
            }
            componentRef = default;
            return false;
        }

        public bool TryComponent<T>(
            EntityHandle entityHandle,
            out ComponentAccessor<T> componentRef
        )
            where T : unmanaged, IEntityComponent
        {
            return TryComponent<T>(entityHandle.ToIndex(_entitiesDb), out componentRef);
        }

        /// <summary>
        /// Returns a <see cref="ComponentAccessor{T}"/> for the global (singleton) entity.
        /// Use this for world-wide state that isn't tied to any specific entity.
        /// </summary>
        public ComponentAccessor<T> GlobalComponent<T>()
            where T : unmanaged, IEntityComponent
        {
            return Component<T>(_worldInfo.GlobalEntityIndex);
        }

        internal EntityAccessor Entity(EntityIndex entityIndex)
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
        /// Structural operations (Add/Remove/MoveTo) and set mutations on the returned
        /// accessor are only allowed from <see cref="AccessorRole.Fixed"/> /
        /// <see cref="AccessorRole.Unrestricted"/> roles, including against
        /// <see cref="VariableUpdateOnlyAttribute"/> templates — VUO templates can
        /// only have entities added / removed via the main-thread path, not via jobs.
        /// Read-only operations (entity reference resolution, shared pointer
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
        /// Returns a lazy-sync gateway for an entity set registered on the
        /// WorldBuilder via AddSet&lt;T&gt;(). The returned <see cref="SetAccessor{T}"/>
        /// selects the timing mode via its properties:
        /// <list type="bullet">
        ///   <item><description><c>.Defer</c> — queue Add / Remove / Clear for next submission (no sync).</description></item>
        ///   <item><description><c>.Read</c> — synchronous read view (syncs writers once at acquisition).</description></item>
        ///   <item><description><c>.Write</c> — synchronous read+write view (syncs all jobs once at acquisition).</description></item>
        /// </list>
        /// Cache the inner view (<c>.Read</c> / <c>.Write</c>) for repeated access in tight loops.
        /// </summary>
        public SetAccessor<T> Set<T>()
            where T : struct, IEntitySet
        {
            return new SetAccessor<T>(this);
        }

        /// <summary>
        /// Returns an untyped gateway for a set referenced by runtime <see cref="SetId"/>.
        /// Use the typed <see cref="Set{T}"/> overload when the set type is known at compile time —
        /// this overload is intended for tooling and editor code that handles sets generically.
        /// </summary>
        public SetByIdAccessor Set(SetId setId)
        {
            return new SetByIdAccessor(this, setId);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref NativeSetDeferredQueues GetSetDeferredQueues(SetId setId)
        {
            AssertCanMakeStructuralChanges();
            return ref _structuralOps.GetDeferredQueues(setId);
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

        internal EntityHandle GetEntityHandle(EntityIndex entityIndex)
        {
            return entityIndex.ToHandle(_entitiesDb);
        }

        internal EntityIndex GetEntityIndex(EntityHandle entityHandle)
        {
            return entityHandle.ToIndex(_entitiesDb);
        }

        internal bool TryGetEntityIndex(EntityHandle entityHandle, out EntityIndex entityIndex)
        {
            return entityHandle.TryToIndex(_entitiesDb, out entityIndex);
        }

        /// <summary>
        /// Enqueues an input component value for the next fixed-update frame. Only callable from
        /// <see cref="SystemPhase.Input"/> systems. The value is applied to the entity's
        /// <see cref="InputAttribute"/> component at the start of the next fixed-update tick.
        /// </summary>
        public void AddInput<T>(EntityHandle entityHandle, in T value)
            where T : unmanaged, IEntityComponent
        {
            AssertCanAddInputsSystem();

            _entityInputQueue.AddInput(_systemRunner.FixedFrame, entityHandle, value);
        }

        internal void AddInput<T>(EntityIndex entityIndex, in T value)
            where T : unmanaged, IEntityComponent
        {
            AssertCanAddInputsSystem();

            _entityInputQueue.AddInput(
                _systemRunner.FixedFrame,
                GetEntityHandle(entityIndex),
                value
            );
        }

        internal NativeComponentBufferRead<T> GetBufferRead<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            AssertIsCurrentlyExecutingAccessor();
            AssertCanReadComponent<T>(group);
            SyncAndRecordRead<T>(group);
            AssertGroupHasComponent<T>(group);
            var nb = _entitiesDb.QuerySingleBuffer<T>(group);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = SafetyManager.GetReadHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
            return new NativeComponentBufferRead<T>(nb, safety);
#else
            return new NativeComponentBufferRead<T>(nb);
#endif
        }

        internal NativeComponentBufferWrite<T> GetBufferWrite<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            AssertIsCurrentlyExecutingAccessor();
            AssertCanWriteComponent<T>(group);
            SyncAndRecord<T>(group);
            AssertGroupHasComponent<T>(group);
            var nb = _entitiesDb.QuerySingleBuffer<T>(group);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = SafetyManager.GetWriteHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
            return new NativeComponentBufferWrite<T>(nb, safety);
#else
            return new NativeComponentBufferWrite<T>(nb);
#endif
        }

        [Conditional("DEBUG")]
        void AssertGroupHasComponent<T1>(GroupIndex group)
            where T1 : unmanaged, IEntityComponent
        {
            Assert.That(
                _worldInfo.GroupHasComponent<T1>(group),
                "GroupIndex {} does not contain component {} (system {}). The query that resolved this group is missing a constraint that ensures every matched group contains {}. Add .WithComponents<{}>() to the query, narrow it via tags/sets, or set MatchByComponents = true on the iteration attribute.",
                group,
                typeof(T1),
                DebugName,
                typeof(T1),
                typeof(T1)
            );
        }

        // Component access rules — see docs/advanced/heap-allocation-rules.md
        // for the full matrix. In short:
        //   - [VariableUpdateOnly] components are render-only territory: only
        //     Variable-role / input-system / Unrestricted-role accessors may
        //     read or write them.
        //   - A template declared [VariableUpdateOnly] propagates VUO to every
        //     component on it: same rules apply.
        //   - Non-[VariableUpdateOnly] components are simulation state: any
        //     accessor may read, only Fixed-role (or Unrestricted-role) may write.
        //   - [Constant] components are immutable after entity creation.
        //     Init-time writes go through EntityInitializer.SetRawImpl, which
        //     bypasses this path; any write that reaches GetBufferWrite /
        //     GetComponentWrite is therefore post-creation and illegal.
        [Conditional("DEBUG")]
        void AssertCanWriteComponent<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            var template = _worldInfo.GetResolvedTemplateForGroup(group);
            var dec = template.TryGetComponentDeclaration(typeof(T));

            // AssertGroupHasComponent reports the missing-component case with a
            // better message; skip rather than asserting twice.
            if (dec == null)
            {
                return;
            }

            Assert.That(
                !dec.IsConstant,
                "Cannot write [Constant] component {} (context {}). Constant components are immutable after entity creation — set them via the EntityInitializer at AddEntity time instead.",
                typeof(T),
                DebugName
            );

            if (IsUnrestricted)
            {
                return;
            }

            Assert.That(
                !dec.IsInput,
                "Cannot write [Input] component {} from {}-role accessor {}. Input components are externally driven — values must be supplied via World.AddInput<T>(...) inside an [ExecuteIn(SystemPhase.Input)] system so that recording / playback can replay them deterministically.",
                typeof(T),
                _role,
                DebugName
            );

            if (IsFixed)
            {
                Assert.That(
                    !template.IsVariableUpdateOnly(dec),
                    "Cannot write [VariableUpdateOnly] component {} from Fixed-role accessor {} (template {}{}). [VariableUpdateOnly] components are render-rate state and can only be touched by Variable-role / Input-system / Unrestricted-role accessors.",
                    typeof(T),
                    DebugName,
                    template.DebugName,
                    template.VariableUpdateOnly ? " — template-level VUO" : ""
                );
            }
            else
            {
                Assert.That(
                    template.IsVariableUpdateOnly(dec),
                    "Cannot write component {} from {}-role accessor {}. Components written outside the Fixed role must be declared [VariableUpdateOnly] on the field or template — they are otherwise treated as simulation state and writing them at render cadence breaks determinism for snapshots and replay.",
                    typeof(T),
                    _role,
                    DebugName
                );
            }
        }

        // While a Fixed-role system is executing, only its own accessor may
        // touch ECS state. Unrestricted-role accessors and other-system
        // accessors are both rejected — they scramble debug attribution by
        // recording access under the wrong DebugName, and Unrestricted-role
        // accessors smuggle
        // non-deterministic state in besides. The SystemRunner tracks the
        // currently-executing Fixed accessor's id; it leaves the tracker
        // at 0 outside Fixed execute, in which case this assertion is a
        // no-op.
        [Conditional("DEBUG")]
        void AssertIsCurrentlyExecutingAccessor()
        {
            var currentId = _systemRunner.CurrentlyExecutingAccessorId;
            if (currentId == 0)
            {
                return;
            }

            Assert.That(
                Id == currentId,
                "Accessor {} cannot be used during Fixed-role system execute. Only the currently-executing system's own accessor may touch ECS state — pass the system's WorldAccessor down rather than holding a separate accessor on a service or capturing one from another system.",
                DebugName
            );
        }

        [Conditional("DEBUG")]
        void AssertCanReadComponent<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            // Only Fixed-role gets gated on reads — all other roles
            // (Variable / None) and input-system accessors read everything.
            // None implies !IsFixed, so the one check covers both.
            if (!IsFixed)
            {
                return;
            }

            var template = _worldInfo.GetResolvedTemplateForGroup(group);
            var dec = template.TryGetComponentDeclaration(typeof(T));

            if (dec == null)
            {
                return;
            }

            Assert.That(
                !template.IsVariableUpdateOnly(dec),
                "Cannot read [VariableUpdateOnly] component {} from Fixed-role accessor {} (template {}{}). [VariableUpdateOnly] components carry non-deterministic render-rate state, so reading them from Fixed would let that non-determinism leak into the simulation.",
                typeof(T),
                DebugName,
                template.DebugName,
                template.VariableUpdateOnly ? " — template-level VUO" : ""
            );
        }

        // ForJobScheduling variants — exposed to source-generated code via the
        // public extension methods in Trecs.Internal.JobGenSchedulingExtensions.
        // Skip SyncMainThread because the caller uses RuntimeJobScheduler
        // to compute dependencies and track the job handle instead.

        internal (NativeComponentBufferRead<T> buffer, int count) GetBufferReadForJobScheduling<T>(
            GroupIndex group
        )
            where T : unmanaged, IEntityComponent
        {
            AssertIsCurrentlyExecutingAccessor();
            AssertCanReadComponent<T>(group);
            AssertGroupHasComponent<T>(group);
            var (nb, count) = _entitiesDb.QuerySingleBufferWithCount<T>(group);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = SafetyManager.GetReadHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
            return (new NativeComponentBufferRead<T>(nb, safety), count);
#else
            return (new NativeComponentBufferRead<T>(nb), count);
#endif
        }

        internal (
            NativeComponentBufferWrite<T> buffer,
            int count
        ) GetBufferWriteForJobScheduling<T>(GroupIndex group)
            where T : unmanaged, IEntityComponent
        {
            AssertIsCurrentlyExecutingAccessor();
            AssertCanWriteComponent<T>(group);
            AssertGroupHasComponent<T>(group);
            var (nb, count) = _entitiesDb.QuerySingleBufferWithCount<T>(group);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = SafetyManager.GetWriteHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                group
            );
            return (new NativeComponentBufferWrite<T>(nb, safety), count);
#else
            return (new NativeComponentBufferWrite<T>(nb), count);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SyncAndRecord<T>(GroupIndex group)
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
        void SyncAndRecordRead<T>(GroupIndex group)
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
            AssertIsCurrentlyExecutingAccessor();
            AssertCanReadComponent<T>(entityIndex.GroupIndex);
            SyncAndRecordRead<T>(entityIndex.GroupIndex);
            var buf = _entitiesDb.QueryEntitiesAndIndex<T>(entityIndex, out var index);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = SafetyManager.GetReadHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                entityIndex.GroupIndex
            );
            return new(new NativeComponentBufferRead<T>(buf, safety), (int)index);
#else
            return new(new NativeComponentBufferRead<T>(buf), (int)index);
#endif
        }

        internal NativeComponentWrite<T> GetComponentWrite<T>(EntityIndex entityIndex)
            where T : unmanaged, IEntityComponent
        {
            AssertIsCurrentlyExecutingAccessor();
            AssertCanWriteComponent<T>(entityIndex.GroupIndex);
            SyncAndRecord<T>(entityIndex.GroupIndex);
            var buf = _entitiesDb.QueryEntitiesAndIndex<T>(entityIndex, out var index);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safety = SafetyManager.GetWriteHandle(
                ResourceId.Component(ComponentTypeId<T>.Value),
                entityIndex.GroupIndex
            );
            return new(new NativeComponentBufferWrite<T>(buf, safety), (int)index);
#else
            return new(new NativeComponentBufferWrite<T>(buf), (int)index);
#endif
        }

        internal ref EntitySetStorage GetSetDirect(EntitySet entitySet)
        {
            return ref _structuralOps.GetSet(entitySet);
        }

        /// <summary>
        /// Returns a set reference for job scheduling (no sync).
        /// Used by generated scheduling code to avoid unnecessary sync points.
        /// </summary>
        internal ref EntitySetStorage GetSetForJobScheduling<T>()
            where T : struct, IEntitySet
        {
            return ref _structuralOps.GetSet(EntitySet<T>.Value);
        }

        internal ref EntitySetStorage GetSetForJobScheduling(SetId setId)
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
                var registered = setCollection._registeredGroups;
                for (int i = 0; i < registered.Length; i++)
                {
                    _systemRunner.JobScheduler.SyncMainThreadForRead(resourceId, registered[i]);
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
            AssertCanMakeStructuralChanges();
            ref var setCollection = ref _structuralOps.GetSet(setId);

            if (_systemRunner.JobScheduler.HasOutstandingJobs)
            {
                var resourceId = ResourceId.Set(setId);
                var registered = setCollection._registeredGroups;
                for (int i = 0; i < registered.Length; i++)
                {
                    _systemRunner.JobScheduler.SyncMainThread(resourceId, registered[i]);
                }
            }

            setCollection.FlushJobWrites();
        }

        internal ref EntitySetStorage GetSetCollection(SetId setId)
        {
            return ref _structuralOps.GetSet(setId);
        }

        internal EntityHandleMap GetEntityHandleMap()
        {
            return _entitiesDb.GetEntityHandleMap();
        }

        internal NativeEntityHandleBuffer GetEntityHandleBufferForJobScheduling(GroupIndex group)
        {
            var handleMap = GetEntityHandleMap();
            var groupList = handleMap._entityIndexToReferenceMap[group.Index];
            if (!groupList.IsCreated)
                return default;
            unsafe
            {
                return new NativeEntityHandleBuffer(
                    new NativeBuffer<int>(groupList.Ptr, groupList.Length),
                    new NativeBuffer<EntityHandleMapElement>(handleMap._entityHandleMap)
                );
            }
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
            ReadOnlyFastList<GroupIndex> groups,
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
            ReadOnlyFastList<GroupIndex> groups,
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

        /// <summary>
        /// Total number of systems registered in this world. Stable for the lifetime of
        /// the world; system indices are in the range <c>[0, SystemCount)</c> and can be
        /// used with <see cref="GetSystemMetadata"/> and <see cref="SetSystemPaused"/>.
        /// </summary>
        public int SystemCount
        {
            get { return _systemRunner.Systems.Count; }
        }

        /// <summary>
        /// Returns metadata for the system at <paramref name="systemIndex"/>. Use this to
        /// iterate systems and build custom groupings (e.g. "all systems matching some
        /// game tag") that drive <see cref="SetSystemPaused"/> calls.
        /// </summary>
        public SystemMetadata GetSystemMetadata(int systemIndex)
        {
            var systems = _systemRunner.Systems;
            Assert.That(
                systemIndex >= 0 && systemIndex < systems.Count,
                "System index {} out of range [0, {})",
                systemIndex,
                systems.Count
            );
            return systems[systemIndex].Metadata;
        }

        /// <summary>
        /// <b>Deterministic</b> pause / resume. Paused state IS in snapshots and recordings —
        /// it survives save / restore and replays the same way every time. Use this for
        /// gameplay pause: pause menus, networked freeze frames, anything that's part of
        /// "the game state".
        /// <para>
        /// For non-deterministic toggles (editor inspector, recording-replay input silencing,
        /// debug menus), use <see cref="SetSystemEnabled"/> with an
        /// <see cref="EnableChannel"/> instead.
        /// </para>
        /// <para>
        /// Must be called from a context that can mutate deterministic state — i.e. from a
        /// fixed-update system, a reactive event handler, or initialization. Calling from a
        /// variable / input system would change the simulation behind the recording's back.
        /// </para>
        /// </summary>
        public void SetSystemPaused(int systemIndex, bool paused)
        {
            AssertCanMakeStructuralChanges();
            _systemEnableState.SetSystemPaused(systemIndex, paused);
        }

        /// <summary>
        /// Returns whether the system at <paramref name="systemIndex"/> is currently paused
        /// (deterministic, see <see cref="SetSystemPaused"/>).
        /// </summary>
        public bool IsSystemPaused(int systemIndex)
        {
            return _systemEnableState.IsSystemPaused(systemIndex);
        }

        /// <summary>
        /// <b>Non-deterministic</b> on / off toggle for a system, scoped to a
        /// <see cref="EnableChannel"/>. Channel state is ephemeral — NOT in snapshots, NOT
        /// in recordings, NOT replayed. Use this for editor toggles, recording-replay input
        /// silencing, debug menus, and anything else that's host-side configuration rather
        /// than game state.
        /// <para>
        /// A system runs only when ALL channels have it enabled AND it is not paused via
        /// <see cref="SetSystemPaused"/>. Multiple channels are AND-combined so independent
        /// callers (editor inspector + replay system) can disable the same system without
        /// stepping on each other's state.
        /// </para>
        /// </summary>
        public void SetSystemEnabled(int systemIndex, EnableChannel channel, bool enabled)
        {
            AssertCanAccessVariableData();
            _systemEnableState.SetSystemEnabled(systemIndex, channel, enabled);
        }

        /// <summary>
        /// Returns whether the given <paramref name="channel"/> currently has the system
        /// enabled (non-deterministic, see <see cref="SetSystemEnabled"/>).
        /// </summary>
        public bool IsSystemEnabled(int systemIndex, EnableChannel channel)
        {
            return _systemEnableState.IsSystemEnabled(systemIndex, channel);
        }

        /// <summary>
        /// Returns true iff the system would run on the next tick — i.e. no
        /// <see cref="EnableChannel"/> has it disabled and it is not paused.
        /// Convenience for debug UIs and tests that want a single "is this system
        /// actually live" query.
        /// </summary>
        public bool IsSystemEffectivelyEnabled(int systemIndex)
        {
            return _systemEnableState.IsSystemEffectivelyEnabled(systemIndex);
        }

        [Conditional("DEBUG")]
        void AssertCanAddInputsSystem()
        {
            Assert.That(
                IsUnrestricted || IsInput,
                "Attempted to use input-only functionality from a non-Input accessor {}",
                DebugName
            );
        }

        [Conditional("DEBUG")]
        internal void AssertCanMakeStructuralChanges()
        {
            AssertIsCurrentlyExecutingAccessor();

            // Unrestricted-role accessors bypass; otherwise structural changes
            // require a Fixed-role accessor regardless of whether systems
            // are currently executing. Fixed services that init/teardown
            // world state run their structural ops at construction or in
            // Initialize and stay deterministic; reactive observers fire
            // during submission with the registering system's accessor;
            // both are Fixed-role so this rule lets them through. Variable
            // and input-system service accessors don't get a "we're
            // between frames" loophole — that's a footgun. Init-time
            // setup that touches mixed state should use AccessorRole.Unrestricted.
            Assert.That(
                CanMakeStructuralChanges,
                "Attempted to modify fixed state from {} (role {}). Structural changes require a Fixed-role or Unrestricted-role accessor.",
                DebugName,
                _role
            );
        }

        // A Fixed-role accessor that resolves a query to one or more
        // VUO-template groups is leaking render-cadence state into the
        // simulation. Reject at query construction time rather than
        // silently filtering — silent filters hide the underlying bug
        // (the predicate was wrong, not the iteration count). Shared
        // between QueryBuilder and SparseQueryBuilder.
        [Conditional("DEBUG")]
        internal void AssertNoVariableUpdateOnlyGroupsForFixedRole(
            ReadOnlyFastList<GroupIndex> groups
        )
        {
            if (!IsFixed)
            {
                return;
            }

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var template = _worldInfo.GetResolvedTemplateForGroup(group);
                Assert.That(
                    !template.VariableUpdateOnly,
                    "Query from Fixed-role accessor {} resolved to [VariableUpdateOnly] template {} (group {}). VUO templates are render-cadence state and must not be queried from Fixed — narrow the predicate (e.g. add a WithoutTags constraint) or move the query to a Variable / Input system.",
                    DebugName,
                    template.DebugName,
                    group
                );
            }
        }

        // Per-group structural-change rule. The "default" group is sim
        // state and Fixed/Unrestricted-only (per AssertCanMakeStructuralChanges
        // above). A [VariableUpdateOnly] template flips that: its groups
        // are render-cadence so Variable-role, input-system, and
        // Unrestricted-role accessors may add / remove / move entities there,
        // while Fixed
        // is rejected outright (Fixed must not touch VUO state at all).
        [Conditional("DEBUG")]
        internal void AssertCanMakeStructuralChangesToGroup(GroupIndex group)
        {
            AssertIsCurrentlyExecutingAccessor();

            if (IsUnrestricted)
            {
                return;
            }

            var template = _worldInfo.GetResolvedTemplateForGroup(group);

            if (template.VariableUpdateOnly)
            {
                Assert.That(
                    !IsFixed,
                    "Cannot modify VUO template {} from Fixed-role accessor {}. The template is declared [VariableUpdateOnly], so structural changes (add/remove/move) must come from Variable-role / input-system / Unrestricted-role accessors.",
                    template.DebugName,
                    DebugName
                );
                return;
            }

            Assert.That(
                IsFixed,
                "Attempted to modify fixed-template state from {} (role {}, template {}). Structural changes on non-VUO templates require a Fixed-role or Unrestricted-role accessor.",
                DebugName,
                _role,
                template.DebugName
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
                IsUnrestricted || !IsFixed,
                "Attempted to use variable-update functionality from Fixed-role accessor {}",
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

            if (IsFixed)
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
        public static IReadOnlyList<int> GetSortedInputSystems(this WorldAccessor world)
        {
            return world.SortedInputSystems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IReadOnlyList<int> GetSortedEarlyPresentationSystems(this WorldAccessor world)
        {
            return world.SortedEarlyPresentationSystems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IReadOnlyList<int> GetSortedPresentationSystems(this WorldAccessor world)
        {
            return world.SortedPresentationSystems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static IReadOnlyList<int> GetSortedLatePresentationSystems(this WorldAccessor world)
        {
            return world.SortedLatePresentationSystems;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static bool SyncMainThread(
            this WorldAccessor world,
            ComponentId componentId,
            GroupIndex group
        )
        {
            return world.JobScheduler.SyncMainThread(ResourceId.Component(componentId), group);
        }
    }
}
