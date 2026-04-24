using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Trecs.Collections;
using Trecs.Internal;

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
    public class World : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(World));

        readonly EntityQuerier _querier;
        readonly SystemRunner _systemRunner;
        readonly EntitySubmitter _entitySubmitter;
        readonly WorldAccessorRegistry _accessorRegistry;
        readonly WorldSettings _settings;
        readonly Rng _fixedRng;
        readonly Rng _variableUpdateRng;
        readonly WorldInfo _worldInfo;
        readonly EntityInputQueue _entityInputQueue;
        readonly EventsManager _eventsManager;
        readonly EcsHeapAllocator _heapAllocator;
        readonly EcsStructuralOps _structuralOps;
        readonly DisposeGroup _eventSubscriptions = new();
        readonly BlobCache _blobCache;
        readonly InterpolatedPreviousSaverManager _interpolatedPreviousSaverManager;
        readonly ComponentStore _componentStore;
        readonly SystemLoader _systemLoader;
        readonly List<ISystem> _systems;

        bool _hasTriggeredAllRemoveEvents;
        bool _systemAddLocked;
        int _accessorIdCounter = 1; // Start at 1 because zero can be like null
        EntityHandle? _globalEntityHandle;
        bool _isDisposed;
        bool _hasInitialized;
        readonly SetStore _setStore;

        internal World(
            SetStore setStore,
            EntityInputQueue entityInputQueue,
            SystemRunner systemRunner,
            UniqueHeap uniqueHeap,
            FrameScopedUniqueHeap frameScopedUniqueHeap,
            FrameScopedSharedHeap frameScopedSharedHeap,
            FrameScopedNativeSharedHeap nativeFrameScopedSharedHeap,
            NativeUniqueHeap nativeUniqueHeap,
            FrameScopedNativeUniqueHeap frameScopedNativeUniqueHeap,
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
            InterpolatedPreviousSaverManager interpolatedPreviousSaverManager,
            ComponentStore componentStore,
            List<ISystem> systems
        )
        {
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
            _settings = settings ?? new WorldSettings();
            _blobCache = blobCache;
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
                frameScopedNativeUniqueHeap
            );

            _structuralOps = new EcsStructuralOps(
                entitySubmitter,
                worldInfo,
                entitiesDb.GetSets(),
                setStore
            );
        }

        public bool IsDisposed
        {
            get { return _isDisposed; }
        }

        internal BlobCache BlobCache
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

        internal ReadOnlyDenseDictionary<int, WorldAccessor> AllAccessors =>
            _accessorRegistry.AllAccessors;

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
                Assert.That(!_isDisposed);
                return _systemRunner;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal EntitySubmitter EntitySubmitter
        {
            get
            {
                Assert.That(!_isDisposed);
                return _entitySubmitter;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal EntityQuerier EntityQuerier
        {
            get
            {
                Assert.That(!_isDisposed);
                return _querier;
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        internal EventsManager EventsManager
        {
            get
            {
                Assert.That(!_isDisposed);
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
                Assert.That(!_isDisposed);
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
                Assert.That(!_isDisposed);
                return _variableUpdateRng;
            }
        }

        internal EntityHandle GlobalEntityHandle
        {
            get
            {
                Assert.That(!_isDisposed);

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
                Assert.That(!_isDisposed);
                return _heapAllocator.UniqueHeap;
            }
        }

        internal FrameScopedUniqueHeap FrameScopedUniqueHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _heapAllocator.FrameScopedUniqueHeap;
            }
        }

        internal NativeSharedHeap NativeSharedHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _heapAllocator.NativeSharedHeap;
            }
        }

        internal FrameScopedNativeSharedHeap FrameScopedNativeSharedHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _heapAllocator.FrameScopedNativeSharedHeap;
            }
        }

        internal SharedHeap SharedHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _heapAllocator.SharedHeap;
            }
        }

        internal FrameScopedSharedHeap FrameScopedSharedHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _heapAllocator.FrameScopedSharedHeap;
            }
        }

        internal NativeUniqueHeap NativeUniqueHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _heapAllocator.NativeUniqueHeap;
            }
        }

        internal FrameScopedNativeUniqueHeap FrameScopedNativeUniqueHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _heapAllocator.FrameScopedNativeUniqueHeap;
            }
        }

        internal int FixedFrame
        {
            get
            {
                Assert.That(!_isDisposed);
                return _systemRunner.FixedFrame;
            }
        }

        internal EcsHeapAllocator HeapAllocator
        {
            get
            {
                Assert.That(!_isDisposed);
                return _heapAllocator;
            }
        }

        internal EcsStructuralOps StructuralOps
        {
            get
            {
                Assert.That(!_isDisposed);
                return _structuralOps;
            }
        }

        // Keep for external callers (SceneInitializer, etc.)
        internal SharedPtr<T> AllocShared<T>(T blob)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _heapAllocator.AllocShared(blob);
        }

        internal UniquePtr<T> AllocUnique<T>(T value)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _heapAllocator.AllocUnique(value);
        }

        ~World()
        {
            _log.Warning("World class has been garbage collected, don't forget to call Dispose()!");
            Dispose(false);
        }

        /// <summary>
        /// Fires all registered remove-event observers for every existing entity without actually
        /// removing them. Call this before <see cref="Dispose"/> when
        /// <see cref="WorldSettings.TriggerRemoveEventsOnDispose"/> is <c>false</c> and you need
        /// cleanup callbacks to run while accessors are still valid.
        /// </summary>
        public void TriggerAllRemoveEvents()
        {
            Assert.That(!_hasTriggeredAllRemoveEvents);
            _hasTriggeredAllRemoveEvents = true;

            // Note here that we do not actually remove in this case -
            // We just trigger the remove events
            Assert.That(
                _worldInfo.GlobalGroup.Value < _componentStore.GroupEntityComponentsDB.Length
            );

            foreach (
                var (group, groupRemovedObservers) in _eventsManager.ReactiveOnRemovedObservers
            )
            {
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

            _log.Debug("Called all ECS remove callbacks for all {} groups", _worldInfo.AllGroups);
        }

        void Dispose(bool _)
        {
            Assert.That(!_isDisposed, "Attempted to dispose WorldAccessor multiple times");

#if DEBUG && TRECS_IS_PROFILING
            if (_settings.WarnOnUnusedTemplates)
            {
                WarnOnUnusedTemplates();
            }
#endif

            if (_settings.TriggerRemoveEventsOnDispose)
            {
                TriggerAllRemoveEvents();
            }

            _entityInputQueue.Dispose();

            _eventSubscriptions.Dispose();
            _systemRunner.Dispose();
            _heapAllocator.Dispose();

            _entitySubmitter.Dispose();

            _structuralOps.MarkDisposed();

            _blobCache.Dispose();

            _isDisposed = true;
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
                        "Template '{}' was registered but no entities were ever created in any of its groups",
                        resolvedTemplate.DebugName
                    );
                }
            }
        }
#endif

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        void WarmupGroups()
        {
            using (TrecsProfiling.Start("Preallocating Groups"))
            {
                foreach (var group in _worldInfo.AllGroups)
                {
                    var _ = _entitySubmitter.GetOrAddDBGroup(group);
                    var template = _worldInfo.GetResolvedTemplateForGroup(group);

                    using (TrecsProfiling.Start("EntitySubmitter.Preallocate (group {})", group))
                    {
                        _entitySubmitter.Preallocate(group, 1, template.ComponentBuilders);
                    }
                }
            }

            _log.Debug("Initialized {} groups", _worldInfo.AllGroups.Count);

            using (TrecsProfiling.Start("EntitySubmitter.FreezeConfiguration"))
            {
                _entitySubmitter.FreezeConfiguration();
            }
        }

        // NOTE: Usually should not need to call this
        public void SubmitEntities()
        {
            Assert.That(!_isDisposed);
            Assert.That(!_systemRunner.IsExecutingSystems);
            _entitySubmitter.SubmitEntities();
        }

        void ScheduleBuildGlobalEntity()
        {
            var globalTemplate = _worldInfo.GlobalTemplate;

            if (_log.IsTraceEnabled())
            {
                _log.Trace(
                    "Constructing global entity with components {}",
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
                Assert.That(
                    seen.Add(system),
                    "System {} was added multiple times to the world",
                    system.GetType()
                );
#endif

                var systemInternal = (ISystemInternal)system;
                if (systemInternal.World == null)
                {
                    systemInternal.World = CreateAccessor(system.GetType());
                }

                systemInternal.Ready();
            }
        }

        /// <summary>
        /// Registers a system after world construction but before <see cref="Initialize"/>.
        /// Throws if called after initialization.
        /// </summary>
        public void AddSystem(ISystem system)
        {
            Assert.That(!_systemAddLocked);
            _systems.Add(system);
        }

        public void AddSystems(IEnumerable<ISystem> systems)
        {
            Assert.That(!_systemAddLocked);
            _systems.AddRange(systems);
        }

        /// <summary>
        /// Initializes all registered systems, creates the global entity, and performs the first
        /// entity submission. Must be called exactly once after construction and before <see cref="Tick"/>.
        /// </summary>
        public void Initialize()
        {
            Assert.That(!_isDisposed);
            Assert.That(!_hasInitialized);
            _hasInitialized = true;

            _entityInputQueue.Accessor = CreateAccessor("EntityInputQueue");

            _systemRunner.SetEventSubjects(_eventsManager);

            _eventsManager
                .DeserializeCompletedEvent.Subscribe(_systemRunner.OnEcsDeserializeCompleted)
                .AddTo(_eventSubscriptions);

            _entityInputQueue.SetPostApplyInputsSubject(_eventsManager.PostApplyInputsEvent);

            _systemAddLocked = true;
            InitializeSystemAccessors(); // This has to happen before LoadSystems
            _accessorRegistry.Close();

            var loadInfo = _systemLoader.LoadSystems(this, _systems);

            using (TrecsProfiling.Start("SystemRunner.Initialize"))
            {
                _systemRunner.Initialize(this, loadInfo);
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
        }

        /// <summary>
        /// Advances the simulation by one frame. Runs input systems, fixed-update systems (potentially
        /// multiple times to catch up), and variable-update systems, then ticks the blob cache.
        /// </summary>
        public void Tick()
        {
            Assert.That(!_isDisposed);
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
            Assert.That(!_isDisposed);
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
        /// Creates a standalone <see cref="WorldAccessor"/> not bound to any system. Useful for
        /// application-level code that needs to interact with the ECS world outside of systems.
        /// </summary>
        public WorldAccessor CreateAccessor(
            string debugName = null,
#if DEBUG
            [CallerFilePath] string callerFile = "",
#endif
            [CallerMemberName] string callerMember = ""
        )
        {
#if DEBUG
            debugName ??= Path.GetFileNameWithoutExtension(callerFile) + "." + callerMember;
#else
            debugName ??= callerMember;
#endif
            return CreateAccessorImpl(
                isInputSystem: false,
                isFixedSystem: false,
                debugName: debugName
            );
        }

        /// <summary>
        /// Creates a <see cref="WorldAccessor"/> configured for the given system type, inspecting
        /// its attributes to determine update phase (fixed, variable, or input).
        /// </summary>
        public WorldAccessor CreateAccessor(Type ownerType)
        {
            var debugName = ownerType.GetPrettyName();

            var isVariableUpdate =
                ownerType.GetCustomAttributes(typeof(VariableUpdateAttribute), true).Length > 0
                || ownerType.GetCustomAttributes(typeof(LateVariableUpdateAttribute), true).Length
                    > 0;

            var isInputSystem =
                ownerType.GetCustomAttributes(typeof(InputSystemAttribute), true).Length > 0;

            var isFixedSystem = !isVariableUpdate && !isInputSystem;

            return CreateAccessorImpl(
                isInputSystem: isInputSystem,
                isFixedSystem: isFixedSystem,
                debugName: debugName
            );
        }

        /// <summary>
        /// Creates an accessor configured for the given system type, inspecting its attributes
        /// to determine update phase (fixed, variable, input).
        /// </summary>
        public WorldAccessor CreateAccessor<T>()
        {
            return CreateAccessor(typeof(T));
        }

        internal WorldAccessor CreateAccessorImpl(
            bool isInputSystem,
            bool isFixedSystem,
            string debugName
        )
        {
            Assert.IsNotNull(debugName);

            var accessor = new WorldAccessor(
                ClaimAccessorIdInternal(),
                this,
                systemRunnerInfo: _systemRunner,
                heapAllocator: _heapAllocator,
                structuralOps: _structuralOps,
                entitiesDb: _querier,
                worldInfo: _worldInfo,
                eventsManager: _eventsManager,
                fixedRng: _fixedRng,
                variableRng: _variableUpdateRng,
                entityInputQueue: _entityInputQueue,
                isFixedSystem: isFixedSystem,
                isInputSystem: isInputSystem,
                debugName: debugName
            );

            _accessorRegistry.RegisterById(accessor);

            return accessor;
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
        public static BlobCache GetBlobCache(this World world)
        {
            return world.BlobCache;
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
        public static ReadOnlyDenseDictionary<int, WorldAccessor> GetAllAccessors(this World world)
        {
            return world.AllAccessors;
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
            bool isInputSystem,
            bool isFixedSystem,
            string debugName
        )
        {
            return world.CreateAccessorImpl(
                isInputSystem: isInputSystem,
                isFixedSystem: isFixedSystem,
                debugName: debugName
            );
        }
    }
}
