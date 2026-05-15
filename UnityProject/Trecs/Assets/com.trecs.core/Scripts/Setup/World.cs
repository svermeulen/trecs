using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;
using UnityEngine;

namespace Trecs
{
    /// <summary>
    /// Main entry point for the Trecs ECS world. Manages the simulation lifecycle
    /// (Tick/LateTick), entity submission, system execution, and accessor creation.
    /// <para>
    /// Note for maintainers: to avoid circular deps, this class should not be
    /// used by other classes within the library. Should only be used on application side.
    /// </para>
    /// <para>
    /// <b>Thread Safety:</b> World is <b>main-thread-only</b>. All methods including
    /// <c>Tick()</c>, <c>LateTick()</c>, <c>SubmitEntities()</c>, <c>Initialize()</c>,
    /// and <c>Dispose()</c> must be called from the main thread. Job parallelism is
    /// achieved through <see cref="WorldAccessor"/> job scheduling APIs, not by calling
    /// World methods from multiple threads.
    /// </para>
    /// </summary>
    public sealed class World : IDisposable
    {
        readonly TrecsLog _log;

        readonly EntityQuerier _querier;
        readonly SystemRunner _systemRunner;
        readonly EntitySubmitter _entitySubmitter;
        readonly WorldAccessorRegistry _accessorRegistry;
        readonly SerializerRegistry _serializerRegistry;
        IAccessRecorder _accessRecorder;
        readonly WorldSettings _settings;
        readonly Rng _fixedRng;
        readonly Rng _variableUpdateRng;
        readonly WorldInfo _worldInfo;
        readonly EntityInputQueue _entityInputQueue;
        readonly EventsManager _eventsManager;
        readonly EcsHeapAllocator _heapAllocator;
        readonly EcsStructuralOps _structuralOps;
        readonly BlobCache _blobCache;
        readonly NativeBlobBoxPool _nativeBlobBoxPool;
        readonly InterpolatedPreviousSaverManager _interpolatedPreviousSaverManager;
        readonly ComponentStore _componentStore;
        readonly SystemLoader _systemLoader;
        readonly SystemEnableState _systemEnableState = new();
        readonly List<ISystem> _systems;

        bool _hasRemovedAllEntities;
        bool _initializeCompleted;
        bool _systemAddLocked;
        int _accessorIdCounter = 1; // Start at 1 because zero can be like null
        EntityHandle? _globalEntityHandle;
        bool _isDisposed;
        bool _hasInitialized;
        readonly SetStore _setStore;

        internal World(
            TrecsLog log,
            SetStore setStore,
            EntityInputQueue entityInputQueue,
            SystemRunner systemRunner,
            UniqueHeap uniqueHeap,
            FrameScopedUniqueHeap frameScopedUniqueHeap,
            FrameScopedSharedHeap frameScopedSharedHeap,
            FrameScopedNativeSharedHeap nativeFrameScopedSharedHeap,
            NativeUniqueHeap nativeUniqueHeap,
            FrameScopedNativeUniqueHeap frameScopedNativeUniqueHeap,
            NativeChunkStore nativeUniqueChunkStore,
            TrecsListHeap trecsListHeap,
            WorldAccessorRegistry accessorRegistry,
            SystemLoader systemLoader,
            EntitySubmitter entitySubmitter,
            EntityQuerier entitiesDb,
            WorldInfo worldInfo,
            EventsManager eventsManager,
            NativeSharedHeap nativeSharedHeap,
            SharedHeap sharedHeap,
            WorldSettings settings,
            BlobCache blobCache,
            NativeBlobBoxPool nativeBlobBoxPool,
            InterpolatedPreviousSaverManager interpolatedPreviousSaverManager,
            ComponentStore componentStore,
            List<ISystem> systems,
            SerializerRegistry serializerRegistry
        )
        {
            _log = log;
            _setStore = setStore;
            _entityInputQueue = entityInputQueue;
            _systemLoader = systemLoader;
            _systemRunner = systemRunner;
            _systems = systems;
            _accessorRegistry = accessorRegistry;
            _entitySubmitter = entitySubmitter;
            _querier = entitiesDb;
            _componentStore = componentStore;
            _eventsManager = eventsManager;
            TrecsAssert.IsNotNull(nativeBlobBoxPool);
            _settings = settings ?? new WorldSettings();
            _blobCache = blobCache;
            _nativeBlobBoxPool = nativeBlobBoxPool;
            _fixedRng = new Rng(_settings.RandomSeed);
            _interpolatedPreviousSaverManager = interpolatedPreviousSaverManager;

            // Not important that we use a deterministic seed for variable rng
            // but also no harm in doing so
            _variableUpdateRng = _fixedRng.Fork();
            _worldInfo = worldInfo;

            _heapAllocator = new EcsHeapAllocator(
                uniqueHeap,
                sharedHeap,
                nativeSharedHeap,
                frameScopedUniqueHeap,
                frameScopedSharedHeap,
                nativeFrameScopedSharedHeap,
                nativeUniqueHeap,
                frameScopedNativeUniqueHeap,
                nativeUniqueChunkStore,
                trecsListHeap
            );

            _structuralOps = new EcsStructuralOps(
                log,
                entitySubmitter,
                worldInfo,
                entitiesDb.GetSets(),
                setStore
            );

            TrecsAssert.IsNotNull(serializerRegistry);
            _serializerRegistry = serializerRegistry;
        }

        /// <summary>
        /// The <see cref="SerializerRegistry"/> backing all save / load,
        /// snapshot, and recording workflows on this world. Pre-populated
        /// with the built-in primitive, math, ECS, and recording-metadata
        /// serializers. Register additional <c>ISerializer&lt;T&gt;</c>
        /// implementations for any managed type stored on the heap
        /// (<c>SharedPtr&lt;T&gt;</c>, <c>UniquePtr&lt;T&gt;</c>, etc.);
        /// blittable types can wrap a <see cref="Trecs.Serialization.BlitSerializer{T}"/>.
        /// Registrations made before <see cref="Initialize"/> are picked up
        /// by all editor tooling; registering later is still fine for
        /// runtime save / load.
        /// </summary>
        public SerializerRegistry SerializerRegistry => _serializerRegistry;

        public bool IsDisposed
        {
            get { return _isDisposed; }
        }

        /// <summary>
        /// Optional human-readable name for this world. Surfaced by editor tooling
        /// (e.g. the World dropdown in <c>TrecsPlayerWindow</c>) when present.
        /// Null by default.
        /// </summary>
        public string DebugName { get; set; }

        /// <summary>
        /// The single <see cref="TrecsLog"/> instance shared by this world and every
        /// framework class it owns. User systems should obtain it via
        /// <see cref="WorldAccessor.Log"/> rather than this property.
        /// </summary>
        public TrecsLog Log => _log;

        public BlobCache BlobCache
        {
            get { return _blobCache; }
        }

        internal EntityInputQueue EntityInputQueue
        {
            get { return _entityInputQueue; }
        }

        internal ComponentStore ComponentStore
        {
            get { return _componentStore; }
        }

        internal SetStore SetStore
        {
            get { return _setStore; }
        }

        public ISimpleObservable SubmissionCompletedEvent =>
            _entitySubmitter.SubmissionCompletedEvent;

        internal WorldAccessor GetAccessorById(int id) => _accessorRegistry.GetAccessorById(id);

        internal ReadOnlyDenseDictionary<ISystem, WorldAccessor> ExecuteAccessors =>
            _accessorRegistry.ExecuteAccessors;

        internal ReadOnlyDenseDictionary<int, WorldAccessor> AccessorsById =>
            _accessorRegistry.AccessorsById;

        // Expose WorldInfo since this is often needed inside the accessor declaration method
        public WorldInfo WorldInfo
        {
            get { return _worldInfo; }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal SystemRunner SystemRunner
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _systemRunner;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal SystemEnableState SystemEnableState
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _systemEnableState;
            }
        }

        /// <summary>
        /// Returns the registry entry for every system in this world, in registration
        /// order. The returned list is stable for the lifetime of the world; indices
        /// match <see cref="SystemEntry.DeclarationIndex"/> and can be used with
        /// <see cref="WorldAccessor.SetSystemEnabled"/> /
        /// <see cref="WorldAccessor.SetSystemPaused"/>. Use this to build custom
        /// groupings (e.g. "all systems matching some game tag") that drive enable /
        /// pause calls.
        /// </summary>
        public IReadOnlyList<SystemEntry> GetSystems()
        {
            TrecsAssert.That(!_isDisposed);
            return _systemRunner.Systems;
        }

        /// <summary>
        /// Returns true iff the system would run on the next tick — i.e. no
        /// <see cref="EnableChannel"/> has it disabled and it is not paused via
        /// <see cref="WorldAccessor.SetSystemPaused"/>. Convenience for debug UIs and
        /// tests that want a single "is this system actually live" query.
        /// </summary>
        public bool IsSystemEffectivelyEnabled(int systemIndex)
        {
            TrecsAssert.That(!_isDisposed);
            return _systemEnableState.IsSystemEffectivelyEnabled(systemIndex);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal EntitySubmitter EntitySubmitter
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _entitySubmitter;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal EntityQuerier EntityQuerier
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _querier;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal EventsManager EventsManager
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _eventsManager;
            }
        }

        /// <summary>
        /// Do not use this inside systems!  Use the Rng from Accessor instead
        /// This is important so that variable systems use a different rng compared to fixed
        /// </summary>
        internal Rng FixedRng
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _fixedRng;
            }
        }

        /// <summary>
        /// Do not use this inside systems!  Use the Rng from Accessor instead
        /// This is important so that variable systems use a different rng compared to fixed
        /// </summary>
        internal Rng VariableRng
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _variableUpdateRng;
            }
        }

        internal EntityHandle GlobalEntityHandle
        {
            get
            {
                TrecsAssert.That(!_isDisposed);

                if (!_globalEntityHandle.HasValue)
                {
                    _globalEntityHandle = _worldInfo.GlobalEntityIndex.ToHandle(_querier);
                }

                return _globalEntityHandle.Value;
            }
        }

        // Keep these for external callers (WorldStateSerializer, SceneInitializer, etc.)
        internal UniqueHeap UniqueHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.UniqueHeap;
            }
        }

        internal FrameScopedUniqueHeap FrameScopedUniqueHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.FrameScopedUniqueHeap;
            }
        }

        internal NativeSharedHeap NativeSharedHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.NativeSharedHeap;
            }
        }

        internal FrameScopedNativeSharedHeap FrameScopedNativeSharedHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.FrameScopedNativeSharedHeap;
            }
        }

        internal SharedHeap SharedHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.SharedHeap;
            }
        }

        internal FrameScopedSharedHeap FrameScopedSharedHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.FrameScopedSharedHeap;
            }
        }

        internal NativeUniqueHeap NativeUniqueHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.NativeUniqueHeap;
            }
        }

        internal FrameScopedNativeUniqueHeap FrameScopedNativeUniqueHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.FrameScopedNativeUniqueHeap;
            }
        }

        internal NativeChunkStore NativeUniqueChunkStore
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.NativeUniqueChunkStore;
            }
        }

        internal TrecsListHeap TrecsListHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator.TrecsListHeap;
            }
        }

        internal int FixedFrame
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _systemRunner.FixedFrame;
            }
        }

        internal EcsHeapAllocator HeapAllocator
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _heapAllocator;
            }
        }

        internal EcsStructuralOps StructuralOps
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _structuralOps;
            }
        }

        ~World()
        {
            // Warning only — do NOT call Dispose from the finalizer thread:
            // Dispose touches managed event subscriptions and frees native heaps,
            // both of which are unsafe from the GC thread (allocator state can be
            // mid-mutation on the main thread, AtomicSafetyHandles can fire on the
            // wrong thread, and event subscriptions can race). Native memory leaks
            // if the user forgets Dispose() — this warning surfaces the bug; clean
            // shutdown is the user's responsibility.
            Debug.LogWarning(
                "World class has been garbage collected, don't forget to call Dispose()!"
            );
        }

        /// <summary>
        /// Fires reactive <c>OnRemoved</c> observers for every non-global entity and zeros
        /// out the per-group entity counts so subsequent queries see an empty world.
        /// The backing component storage is not freed — the heap allocator tears it down
        /// during <see cref="Dispose"/>; this method only flips the logical view.
        /// <para>
        /// The global singleton entity is intentionally untouched: its lifecycle is
        /// co-terminus with the <see cref="World"/>, not with normal entity removal, so
        /// firing <c>OnRemoved</c> for it would lie to subscribers, and zeroing its
        /// count would break the user contract that the global entity remains queryable
        /// and mutable from <c>OnShutdown</c>.
        /// </para>
        /// <para>
        /// Called automatically from <see cref="Dispose"/> when
        /// <see cref="WorldSettings.RemoveAllEntitiesOnDispose"/> is true. Call it
        /// manually when that setting is false and you need cleanup callbacks to run
        /// while accessors are still valid.
        /// </para>
        /// </summary>
        public void RemoveAllEntities()
        {
            TrecsAssert.That(!_hasRemovedAllEntities);
            _hasRemovedAllEntities = true;

            TrecsAssert.That(
                !_worldInfo.GlobalGroup.IsNull
                    && _worldInfo.GlobalGroup.Index < _componentStore.GroupEntityComponentsDB.Length
            );

            var globalGroup = _worldInfo.GlobalGroup;

            // Pass 1: fire OnRemoved for every observed non-global group.
            foreach (
                var (group, groupRemovedObservers) in _eventsManager.ReactiveOnRemovedObservers
            )
            {
                if (group == globalGroup)
                {
                    continue;
                }

                var count = _querier.CountEntitiesInGroup(group);

                if (count > 0)
                {
                    var rangeValues = new EntityRange(0, count);

                    for (var i = 0; i < groupRemovedObservers.Count; i++)
                    {
                        groupRemovedObservers[i].Observer(group, rangeValues);
                    }
                }
            }

            // Pass 2: zero out per-component-array counts on every non-global group, so any
            // Query() / Count() / [ForEachEntity] call from a later OnShutdown hook sees an
            // empty world. Component storage is left allocated; the heap allocator frees it
            // during the rest of Dispose. We must iterate ALL groups here (not just observed
            // ones) — a group with zero OnRemoved observers still needs its count zeroed so
            // non-reactive queries return empty too.
            var groupComponentsDB = _componentStore.GroupEntityComponentsDB;
            var globalGroupIndex = globalGroup.Index;
            for (int g = 0; g < groupComponentsDB.Length; g++)
            {
                if (g == globalGroupIndex)
                {
                    continue;
                }

                foreach (var (_, componentArray) in groupComponentsDB[g])
                {
                    componentArray.SetCount(0);
                }
            }

            _log.Debug(
                "Removed all non-global entities for all {0} groups",
                _worldInfo.AllGroups.Count
            );
        }

        public void Dispose()
        {
            TrecsAssert.That(!_isDisposed, "Attempted to dispose World multiple times");

            WorldRegistry.Unregister(this);

#if DEBUG && TRECS_IS_PROFILING
            if (_settings.WarnOnUnusedTemplates)
            {
                WarnOnUnusedTemplates();
            }
#endif

            if (_settings.RemoveAllEntitiesOnDispose && _initializeCompleted)
            {
                RemoveAllEntities();
            }

            if (_initializeCompleted)
            {
                CallSystemShutdownHooks();
            }

            _entityInputQueue.Dispose();

            _systemRunner.Dispose();
            _systemEnableState.Dispose();
            _heapAllocator.Dispose();

            _entitySubmitter.Dispose();

            _structuralOps.MarkDisposed();

            _blobCache.Dispose();

            // Pool is disposed last — heaps and blob stores both return boxes to it
            // during their own Dispose, so it must outlive everything that holds boxes.
            _nativeBlobBoxPool.Dispose();

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }

#if DEBUG && TRECS_IS_PROFILING
        void WarnOnUnusedTemplates()
        {
            var usedGroups = _entitySubmitter._groupsWithEntitiesEverAdded;

            foreach (var resolvedTemplate in _worldInfo.ResolvedTemplates)
            {
                if (
                    resolvedTemplate.Template == TrecsTemplates.Globals.Template
                    || resolvedTemplate.AllBaseTemplates.Contains(TrecsTemplates.Globals.Template)
                )
                {
                    continue;
                }

                bool anyGroupUsed = false;
                foreach (var group in resolvedTemplate.Groups)
                {
                    if (usedGroups.Contains(group))
                    {
                        anyGroupUsed = true;
                        break;
                    }
                }

                if (!anyGroupUsed)
                {
                    _log.Warning(
                        "Template '{0}' was registered but no entities were ever created in any of its groups",
                        resolvedTemplate.DebugName
                    );
                }
            }
        }
#endif

        void WarmupGroups()
        {
            // Per-group component-array slots are now materialized lazily —
            // the first entity into a group triggers the IComponentArray
            // creation via ComponentStore.GetOrAddTypeSafeDictionary, the
            // staging buffer slots via EntityFactory.AddEntity, and the
            // reference list growth via the locator. Skipping the eager
            // warmup avoids O(groups × components) startup allocations
            // for templates with many partitions; the per-group startup
            // cost is paid only for groups that actually get populated.

            _log.Debug("Registered {0} groups (lazy buffer init)", _worldInfo.AllGroups.Count);

            using (TrecsProfiling.Start("EntitySubmitter.FreezeConfiguration"))
            {
                _entitySubmitter.FreezeConfiguration();
            }
        }

        // NOTE: Usually should not need to call this
        public void SubmitEntities()
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(!_systemRunner.IsExecutingSystems);
            _entitySubmitter.SubmitEntities();
        }

        void ScheduleBuildGlobalEntity()
        {
            var globalTemplate = _worldInfo.GlobalTemplate;

            if (_log.IsTraceEnabled())
            {
                _log.Trace(
                    "Constructing global entity with components {0}",
                    globalTemplate
                        .ComponentDeclarations.Select(c => c.ComponentType.ToString())
                        .Join(", ")
                );
            }

            EntityInitializer initializer;

            using (TrecsProfiling.Start("AddEntity"))
            {
                initializer = _entitySubmitter.AddEntity(
                    _worldInfo.GlobalEntityIndex.GroupIndex,
                    globalTemplate.ComponentBuilders,
                    globalTemplate.DebugName
                );
            }

            initializer.AssertComplete();
        }

        void InitializeSystemAccessors()
        {
#if DEBUG
            var seen = new HashSet<ISystem>();
#endif
            foreach (var system in _systems)
            {
#if DEBUG
                TrecsAssert.That(
                    seen.Add(system),
                    "System {0} was added multiple times to the world",
                    system.GetType()
                );
#endif

                var systemInternal = (ISystemInternal)system;
                if (systemInternal.World == null)
                {
                    systemInternal.World = CreateAccessorForSystem(system.GetType());
                }
            }
        }

        // OnReady runs in runtime execute order: EarlyPresentation → Input → Fixed → Presentation → LatePresentation,
        // with [ExecuteAfter]/[ExecuteBefore]/[ExecutePriority] applied within each phase.
        void CallSystemReadyHooks(SystemLoader.LoadInfo loadInfo)
        {
            void CallReady(int globalIndex)
            {
                var systemInternal = (ISystemInternal)loadInfo.Systems[globalIndex].System;
                systemInternal.Ready();
            }

            foreach (var globalIndex in loadInfo.SortedEarlyPresentationSystems)
            {
                CallReady(globalIndex);
            }
            foreach (var globalIndex in loadInfo.SortedInputSystems)
            {
                CallReady(globalIndex);
            }
            foreach (var globalIndex in loadInfo.SortedFixedSystems)
            {
                CallReady(globalIndex);
            }
            foreach (var globalIndex in loadInfo.SortedPresentationSystems)
            {
                CallReady(globalIndex);
            }
            foreach (var globalIndex in loadInfo.SortedLatePresentationSystems)
            {
                CallReady(globalIndex);
            }
        }

        // Strict reverse of CallSystemReadyHooks: phases in reverse, and within each
        // phase, sorted systems in reverse. Driven from _systemRunner because loadInfo
        // is not retained past Initialize.
        void CallSystemShutdownHooks()
        {
            void CallShutdown(int globalIndex)
            {
                var systemInternal = (ISystemInternal)_systemRunner.Systems[globalIndex].System;
                systemInternal.Shutdown();
            }

            void IterateReverse(IReadOnlyList<int> sorted)
            {
                for (int i = sorted.Count - 1; i >= 0; i--)
                {
                    CallShutdown(sorted[i]);
                }
            }

            IterateReverse(_systemRunner.SortedLatePresentationSystems);
            IterateReverse(_systemRunner.SortedPresentationSystems);
            IterateReverse(_systemRunner.SortedFixedSystems);
            IterateReverse(_systemRunner.SortedInputSystems);
            IterateReverse(_systemRunner.SortedEarlyPresentationSystems);
        }

        /// <summary>
        /// Registers a system after world construction but before <see cref="Initialize"/>.
        /// Throws if called after initialization.
        /// </summary>
        public void AddSystem(ISystem system)
        {
            TrecsAssert.That(!_systemAddLocked);
            _systems.Add(system);
        }

        public void AddSystems(IEnumerable<ISystem> systems)
        {
            TrecsAssert.That(!_systemAddLocked);
            _systems.AddRange(systems);
        }

        /// <summary>
        /// Initializes all registered systems, creates the global entity, and performs the first
        /// entity submission. Must be called exactly once after construction and before <see cref="Tick"/>.
        /// </summary>
        public void Initialize()
        {
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(!_hasInitialized);
            _hasInitialized = true;

            _entityInputQueue.Accessor = CreateAccessor(
                AccessorRole.Unrestricted,
                "EntityInputQueue"
            );

            _systemRunner.SetEventSubjects(_eventsManager);

            _entityInputQueue.SetInputsAppliedSubject(_eventsManager.InputsAppliedEvent);

            _systemAddLocked = true;
            InitializeSystemAccessors(); // This has to happen before LoadSystems
            _accessorRegistry.Close();

            var loadInfo = _systemLoader.LoadSystems(this, _systems);

            _systemEnableState.Initialize(loadInfo.Systems.Count);

            using (TrecsProfiling.Start("SystemRunner.Initialize"))
            {
                _systemRunner.Initialize(this, loadInfo, _systemEnableState);
            }

            _interpolatedPreviousSaverManager.Initialize(this);

            using (TrecsProfiling.Start("WarmupGroups"))
            {
                WarmupGroups();
            }

            using (TrecsProfiling.Start("ScheduleBuildGlobalEntity"))
            {
                ScheduleBuildGlobalEntity();
            }

            using (TrecsProfiling.Start("SubmitGlobalEntity"))
            {
                _entitySubmitter.SubmitEntities();
            }

            CallSystemReadyHooks(loadInfo);

            _initializeCompleted = true;

            // Register only after Initialize completes so editor-tool listeners
            // (TrecsEntitiesWindow et al.) never see a partially-built world.
            // Per-group component dictionaries are materialized lazily on
            // first entity creation, but CountEntitiesInGroup and similar
            // queries already handle the empty-group case.
            WorldRegistry.Register(this);
        }

        /// <summary>
        /// Advances the simulation by one frame. Runs input systems, fixed-update systems (potentially
        /// multiple times to catch up), and variable-update systems, then ticks the blob cache.
        /// </summary>
        public void Tick()
        {
            TrecsAssert.That(!_isDisposed);
            using (TrecsProfiling.Start("SystemRunner.Tick"))
            {
                _systemRunner.Tick();
            }

            _blobCache.Tick();
        }

        /// <summary>
        /// Runs late-variable-update systems. Call after <see cref="Tick"/> each rendered frame.
        /// </summary>
        public void LateTick()
        {
            TrecsAssert.That(!_isDisposed);
            using (TrecsProfiling.Start("SystemRunner.LateTick"))
            {
                _systemRunner.LateTick();
            }
        }

        internal int ClaimAccessorIdInternal()
        {
            var id = _accessorIdCounter;
            _accessorIdCounter += 1;
            return id;
        }

        /// <summary>
        /// Creates a standalone <see cref="WorldAccessor"/> with the given <see cref="AccessorRole"/>.
        /// The role controls component read/write rules, structural-change rules, and heap-allocation
        /// rules — see <see cref="AccessorRole"/> for the full matrix. Pick <see cref="AccessorRole.Unrestricted"/>
        /// only for non-system code (lifecycle hooks, debug tooling, event callbacks, networking,
        /// scripting bridges); runtime gameplay code that runs as part of system execution should
        /// declare a real role so rule violations surface loudly. Manually-created accessors never
        /// gain input-system permissions — input-queue access is only granted to system-owned
        /// accessors created from a system declared with <c>[ExecuteIn(SystemPhase.Input)]</c>.
        /// </summary>
        public WorldAccessor CreateAccessor(
            AccessorRole role,
            string debugName = null,
#if DEBUG
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0,
#endif
            [CallerMemberName] string callerMember = ""
        )
        {
#if DEBUG
            debugName ??= Path.GetFileNameWithoutExtension(callerFile) + "." + callerMember;
#else
            debugName ??= callerMember;
            string callerFile = string.Empty;
            int callerLine = 0;
#endif
            return CreateAccessorImpl(
                role: role,
                isInput: false,
                debugName: debugName,
                createdAtFile: callerFile,
                createdAtLine: callerLine
            );
        }

        /// <summary>
        /// Framework-internal: creates a <see cref="WorldAccessor"/> for an
        /// <see cref="ISystem"/> by reflecting on the system type's
        /// <see cref="ExecuteInAttribute"/>. End-user code should not call this —
        /// systems get their accessor automatically from
        /// <see cref="Initialize"/>; non-system code should use
        /// <see cref="CreateAccessor(AccessorRole, string, string, int, string)"/>
        /// with an explicit role.
        /// </summary>
        internal WorldAccessor CreateAccessorForSystem(Type systemType)
        {
            var debugName = systemType.GetPrettyName();

            var executeInAttribute = (ExecuteInAttribute)
                systemType.GetCustomAttributes(typeof(ExecuteInAttribute), true).SingleOrDefault();

            var phase = executeInAttribute?.Phase ?? SystemPhase.Fixed;

            // System-owned accessors take their identity from the system
            // type — no source-line origin to capture.
            return CreateAccessorImpl(
                role: phase.ToAccessorRole(),
                isInput: phase == SystemPhase.Input,
                debugName: debugName,
                createdAtFile: string.Empty,
                createdAtLine: 0
            );
        }

        internal WorldAccessor CreateAccessorImpl(
            AccessorRole role,
            bool isInput,
            string debugName,
            string createdAtFile,
            int createdAtLine
        )
        {
            TrecsAssert.IsNotNull(debugName);

            var accessor = new WorldAccessor(
                ClaimAccessorIdInternal(),
                this,
                systemRunnerInfo: _systemRunner,
                systemEnableState: _systemEnableState,
                heapAllocator: _heapAllocator,
                structuralOps: _structuralOps,
                entitiesDb: _querier,
                worldInfo: _worldInfo,
                eventsManager: _eventsManager,
                fixedRng: _fixedRng,
                variableRng: _variableUpdateRng,
                entityInputQueue: _entityInputQueue,
                role: role,
                isInput: isInput,
                debugName: debugName,
                createdAtFile: createdAtFile,
                createdAtLine: createdAtLine
            );

            _accessorRegistry.RegisterById(accessor);

            if (_accessRecorder != null)
            {
                accessor.AccessRecorder = _accessRecorder;
            }

            return accessor;
        }

        /// <summary>
        /// Installs (or removes, if <c>null</c>) an
        /// <see cref="IAccessRecorder"/> that the world hands to every
        /// accessor — current and future. Intended for editor / debug tooling
        /// that wants per-system read/write and add/remove/move maps. Pass
        /// <c>null</c> to detach.
        /// </summary>
        public void SetAccessRecorder(IAccessRecorder recorder)
        {
            _accessRecorder = recorder;
            foreach (var kvp in _accessorRegistry.AccessorsById)
            {
                kvp.Value.AccessRecorder = recorder;
            }
            _entitySubmitter.SetAccessRecorder(recorder);
        }
    }
}

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class WorldExtensions
    {
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static SystemRunner GetSystemRunner(this World world)
        {
            return world.SystemRunner;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static EntitySubmitter GetEntitySubmitter(this World world)
        {
            return world.EntitySubmitter;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static EntityQuerier GetEntityQuerier(this World world)
        {
            return world.EntityQuerier;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static EventsManager GetEventsManager(this World world)
        {
            return world.EventsManager;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static EntityInputQueue GetEntityInputQueue(this World world)
        {
            return world.EntityInputQueue;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static UniqueHeap GetUniqueHeap(this World world)
        {
            return world.UniqueHeap;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static SharedHeap GetSharedHeap(this World world)
        {
            return world.SharedHeap;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeSharedHeap GetNativeSharedHeap(this World world)
        {
            return world.NativeSharedHeap;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static NativeUniqueHeap GetNativeUniqueHeap(this World world)
        {
            return world.NativeUniqueHeap;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static FrameScopedNativeUniqueHeap GetFrameScopedNativeUniqueHeap(this World world)
        {
            return world.FrameScopedNativeUniqueHeap;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal static NativeChunkStore GetNativeUniqueChunkStore(this World world)
        {
            return world.NativeUniqueChunkStore;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static TrecsListHeap GetTrecsListHeap(this World world)
        {
            return world.TrecsListHeap;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static WorldAccessor GetAccessorById(this World world, int id)
        {
            return world.GetAccessorById(id);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static ReadOnlyDenseDictionary<ISystem, WorldAccessor> GetExecuteAccessors(
            this World world
        )
        {
            return world.ExecuteAccessors;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static ReadOnlyDenseDictionary<int, WorldAccessor> GetAccessorsById(this World world)
        {
            return world.AccessorsById;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static ComponentStore GetComponentStore(this World world)
        {
            return world.ComponentStore;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static SetStore GetSetStore(this World world)
        {
            return world.SetStore;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static WorldAccessor CreateAccessorExplicit(
            this World world,
            AccessorRole role,
            bool isInput,
            string debugName,
            [CallerFilePath] string callerFile = "",
            [CallerLineNumber] int callerLine = 0
        )
        {
            // Required role + isInput arguments (no defaults) so framework-internal
            // callers — Lua system bridges, custom system loaders — have to declare
            // exactly what they're constructing. isInput should be true iff the
            // owning system is in SystemPhase.Input.
            return world.CreateAccessorImpl(
                role: role,
                isInput: isInput,
                debugName: debugName,
                createdAtFile: callerFile,
                createdAtLine: callerLine
            );
        }
    }
}
