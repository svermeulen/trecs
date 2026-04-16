using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Fluent builder for constructing and configuring a Trecs <see cref="World"/> instance.
    /// </summary>
    public class WorldBuilder
    {
        readonly List<ISystem> _systems = new();
        readonly List<IBlobStore> _blobStores = new();
        readonly List<SystemOrderConstraint> _systemOrderConstraints = new();

        internal readonly List<IInterpolatedPreviousSaver> _interpolatedPreviousSavers = new();
        readonly List<Template> _templates = new();
        readonly List<SetDef> _sets = new();
        internal ISystemMetadataProvider _systemMetadataProvider;

        bool _hasBuilt;
        BlobCacheSettings _blobCacheSettings;
        WorldSettings _settings;
        ITrecsPoolManager _poolManager;

        /// <summary>
        /// Creates a new empty WorldBuilder.
        /// </summary>
        public WorldBuilder() { }

        /// <summary>
        /// Sets the <see cref="WorldSettings"/> for the world being built.
        /// </summary>
        public WorldBuilder SetSettings(WorldSettings settings)
        {
            Assert.IsNotNull(settings);
            Assert.IsNull(_settings, "Settings have already been set");
            _settings = settings;
            return this;
        }

        /// <summary>
        /// Registers a concrete entity type with the world. Each entity type
        /// determines a group (contiguous memory layout) for its component
        /// arrays. Only register entity types that will be instantiated
        /// directly — base templates used via <c>IExtends</c> are discovered
        /// automatically and should not be registered here.
        /// </summary>
        public WorldBuilder AddEntityType(Template template)
        {
            Assert.IsNotNull(template);
            _templates.Add(template);
            return this;
        }

        /// <summary>
        /// Registers an entity set with the world.
        /// </summary>
        public WorldBuilder AddSet<T>()
            where T : struct, IEntitySet
        {
            var setDef = EntitySet<T>.Value;

            // Force EntitySetId<T> static constructor to run on the main thread,
            // so the SharedStatic is populated before any Burst job accesses it.
            // Without this, the [BurstDiscard] on Init() strips the initialization
            // when the static constructor first runs inside a Burst-compiled job.
            _ = EntitySetId<T>.Value;

            Assert.That(
                !_sets.Any(f => f.Id == setDef.Id),
                "Set '{}' is already added to the WorldBuilder",
                setDef.DebugName
            );
            _sets.Add(setDef);

            return this;
        }

        /// <summary>
        /// Registers multiple concrete entity types with the world.
        /// </summary>
        public WorldBuilder AddEntityTypes(IEnumerable<Template> templates)
        {
            Assert.IsNotNull(templates);
            foreach (var template in templates)
            {
                AddEntityType(template);
            }
            return this;
        }

        /// <summary>
        /// Sets a custom pool manager for internal memory allocation.
        /// </summary>
        public WorldBuilder SetPoolManager(ITrecsPoolManager poolManager)
        {
            Assert.IsNotNull(poolManager);
            Assert.IsNull(_poolManager, "PoolManager has already been set");
            _poolManager = poolManager;
            return this;
        }

        /// <summary>
        /// Adds a system to the world.
        /// </summary>
        public WorldBuilder AddSystem(ISystem system)
        {
            Assert.IsNotNull(system);
            _systems.Add(system);
            return this;
        }

        /// <summary>
        /// Adds multiple systems to the world.
        /// </summary>
        public WorldBuilder AddSystems(IEnumerable<ISystem> systems)
        {
            Assert.IsNotNull(systems);
            foreach (var system in systems)
            {
                AddSystem(system);
            }
            return this;
        }

        /// <summary>
        /// Adds a blob store for loading shared blob data.
        /// </summary>
        public WorldBuilder AddBlobStore(IBlobStore store)
        {
            Assert.IsNotNull(store);
            _blobStores.Add(store);
            return this;
        }

        /// <summary>
        /// Adds multiple blob stores for loading shared blob data.
        /// </summary>
        public WorldBuilder AddBlobStores(IEnumerable<IBlobStore> stores)
        {
            Assert.IsNotNull(stores);
            foreach (var store in stores)
            {
                AddBlobStore(store);
            }
            return this;
        }

        /// <summary>
        /// Configures caching behavior for blob data.
        /// </summary>
        public WorldBuilder SetBlobCacheSettings(BlobCacheSettings settings)
        {
            Assert.IsNotNull(settings);
            Assert.IsNull(_blobCacheSettings, "BlobCacheSettings have already been set");
            _blobCacheSettings = settings;
            return this;
        }

        /// <summary>
        /// Adds an ordering constraint specifying that the given system types must execute in the provided order.
        /// </summary>
        public WorldBuilder AddSystemOrderConstraint(params Type[] systemOrder)
        {
            _systemOrderConstraints.Add(new SystemOrderConstraint(systemOrder));
            return this;
        }

        /// <summary>
        /// Adds a pre-built system ordering constraint.
        /// </summary>
        public WorldBuilder AddSystemOrderConstraint(SystemOrderConstraint constraint)
        {
            _systemOrderConstraints.Add(constraint);
            return this;
        }

        /// <summary>
        /// Adds multiple system ordering constraints.
        /// </summary>
        public WorldBuilder AddSystemOrderConstraints(
            IEnumerable<SystemOrderConstraint> constraints
        )
        {
            Assert.IsNotNull(constraints);
            foreach (var constraint in constraints)
            {
                AddSystemOrderConstraint(constraint);
            }
            return this;
        }

#if DEBUG && !TRECS_IS_PROFILING
        void Validate()
        {
            // Check for duplicate templates
            var seenTemplates = new HashSet<Template>();
            foreach (var template in _templates)
            {
                Assert.That(
                    seenTemplates.Add(template),
                    "Duplicate template '{}' added to WorldBuilder",
                    template.DebugName
                );
            }

            // Set tag validation is handled by SetStore.Register* which asserts groups.Count > 0
        }
#endif

        /// <summary>
        /// Builds and immediately initializes the world. Use this when no additional
        /// setup is needed between Build and Initialize. For cases where you need to
        /// add systems or configure the world after building, use Build() followed by
        /// world.Initialize() separately.
        /// </summary>
        public World BuildAndInitialize()
        {
            var world = Build();
            world.Initialize();
            return world;
        }

        /// <summary>
        /// Builds the world without initializing it, allowing further configuration before calling <see cref="World.Initialize"/>.
        /// </summary>
        public World Build()
        {
            Assert.That(!_hasBuilt, "Build() has already been called");
            _hasBuilt = true;

#if DEBUG && !TRECS_IS_PROFILING
            Validate();
#endif

            var settings = _settings ?? new WorldSettings();

            var worldInfo = new WorldInfo(_templates);

            var uniqueHeap = new UniqueHeap(_poolManager);
            var blobCache = new BlobCache(_blobStores, _blobCacheSettings);
            var sharedHeap = new SharedHeap(blobCache);
            var nativeSharedHeap = new NativeSharedHeap(blobCache);
            var frameScopedUniqueHeap = new FrameScopedUniqueHeap(_poolManager);
            var frameScopedSharedHeap = new FrameScopedSharedHeap(blobCache);
            var nativeFrameScopedSharedHeap = new FrameScopedNativeSharedHeap(blobCache);
            var nativeUniqueHeap = new NativeUniqueHeap();
            var frameScopedNativeUniqueHeap = new FrameScopedNativeUniqueHeap();

            var accessorRegistry = new WorldAccessorRegistry();

            _systemMetadataProvider ??= new DefaultSystemMetadataProvider(
                _systemOrderConstraints,
                accessorRegistry
            );

            _systems.Add(new FixedUpdateSystem());

            var systemLoader = new SystemLoader(
                accessorRegistry,
                _systemMetadataProvider,
                worldInfo
            );

            var eventsManager = new EventsManager();

            var componentStore = new ComponentStore();
            var setStore = new SetStore();

            foreach (var setDef in _sets)
            {
                setStore.RegisterSet(setDef, worldInfo);
            }

            var entityQuerier = new EntityQuerier(componentStore, setStore);

            var submitter = new EntitySubmitter(
                worldInfo,
                accessorRegistry,
                eventsManager,
                componentStore,
                setStore,
                settings,
                entityQuerier,
                nativeSharedHeap,
                nativeUniqueHeap,
                frameScopedNativeUniqueHeap
            );

            var interpolatedPreviousSaverManager = new InterpolatedPreviousSaverManager(
                _interpolatedPreviousSavers
            );

            var systemRunner = new SystemRunner(
                submitter,
                settings,
                interpolatedPreviousSaverManager
            );

            var entityInputQueue = new EntityInputQueue(
                frameScopedSharedHeap,
                nativeFrameScopedSharedHeap,
                frameScopedUniqueHeap,
                frameScopedNativeUniqueHeap,
                systemRunner,
                worldInfo
            );

            return new World(
                entityInputQueue: entityInputQueue,
                systemRunner: systemRunner,
                uniqueHeap: uniqueHeap,
                frameScopedUniqueHeap: frameScopedUniqueHeap,
                frameScopedSharedHeap: frameScopedSharedHeap,
                nativeFrameScopedSharedHeap: nativeFrameScopedSharedHeap,
                nativeUniqueHeap: nativeUniqueHeap,
                frameScopedNativeUniqueHeap: frameScopedNativeUniqueHeap,
                accessorRegistry: accessorRegistry,
                entitySubmitter: submitter,
                entitiesDb: entityQuerier,
                worldInfo: worldInfo,
                setStore: setStore,
                systemLoader: systemLoader,
                eventsManager: eventsManager,
                nativeSharedHeap: nativeSharedHeap,
                sharedHeap: sharedHeap,
                settings: settings,
                blobCache: blobCache,
                interpolatedPreviousSaverManager: interpolatedPreviousSaverManager,
                componentStore: componentStore,
                systems: _systems
            );
        }
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// Internal extension methods for interpolation support on <see cref="WorldBuilder"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class WorldBuilderInterpolationExtensions
    {
        /// <summary>
        /// Registers an interpolated previous-frame state saver for smooth visual interpolation.
        /// </summary>
        public static WorldBuilder AddInterpolatedPreviousSaver(
            this WorldBuilder builder,
            IInterpolatedPreviousSaver saver
        )
        {
            builder._interpolatedPreviousSavers.Add(saver);
            return builder;
        }

        /// <summary>
        /// Overrides the default system metadata provider used for system ordering and accessor resolution.
        /// </summary>
        // This isn't officially in api yet
        public static WorldBuilder SetSystemMetadataProvider(
            this WorldBuilder builder,
            ISystemMetadataProvider systemMetadataProvider
        )
        {
            Assert.IsNotNull(systemMetadataProvider);
            builder._systemMetadataProvider = systemMetadataProvider;
            return builder;
        }
    }
}
