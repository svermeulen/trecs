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
            Require.That(settings != null, "settings must not be null");
            Require.That(_settings == null, "Settings have already been set");
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
            Require.That(template != null, "template must not be null");
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

            Require.That(
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
            Require.That(templates != null, "templates must not be null");
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
            Require.That(poolManager != null, "poolManager must not be null");
            Require.That(_poolManager == null, "PoolManager has already been set");
            _poolManager = poolManager;
            return this;
        }

        /// <summary>
        /// Adds a system to the world.
        /// </summary>
        public WorldBuilder AddSystem(ISystem system)
        {
            Require.That(system != null, "system must not be null");
            _systems.Add(system);
            return this;
        }

        /// <summary>
        /// Adds multiple systems to the world.
        /// </summary>
        public WorldBuilder AddSystems(IEnumerable<ISystem> systems)
        {
            Require.That(systems != null, "systems must not be null");
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
            Require.That(store != null, "store must not be null");
            _blobStores.Add(store);
            return this;
        }

        /// <summary>
        /// Adds multiple blob stores for loading shared blob data.
        /// </summary>
        public WorldBuilder AddBlobStores(IEnumerable<IBlobStore> stores)
        {
            Require.That(stores != null, "stores must not be null");
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
            Require.That(settings != null, "settings must not be null");
            Require.That(_blobCacheSettings == null, "BlobCacheSettings have already been set");
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
            Require.That(constraints != null, "constraints must not be null");
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
            try
            {
                world.Initialize();
            }
            catch
            {
                world.Dispose();
                throw;
            }
            return world;
        }

        /// <summary>
        /// Builds the world without initializing it, allowing further configuration before calling <see cref="World.Initialize"/>.
        /// </summary>
        public World Build()
        {
            Require.That(!_hasBuilt, "Build() has already been called");
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

            var componentStore = new ComponentStore(worldInfo.AllGroups.Count);
            var setStore = new SetStore(worldInfo.AllGroups.Count);

            foreach (var setDef in _sets)
            {
                setStore.RegisterSet(setDef, worldInfo);
            }

            var entityQuerier = new EntityQuerier(
                componentStore,
                setStore,
                worldInfo.AllGroups.Count
            );

            var jobScheduler = new RuntimeJobScheduler();

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
                frameScopedNativeUniqueHeap,
                jobScheduler
            );

            var interpolatedPreviousSaverManager = new InterpolatedPreviousSaverManager(
                _interpolatedPreviousSavers
            );

            var systemRunner = new SystemRunner(
                submitter,
                settings,
                interpolatedPreviousSaverManager,
                jobScheduler
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
            Require.That(systemMetadataProvider != null, "systemMetadataProvider must not be null");
            builder._systemMetadataProvider = systemMetadataProvider;
            return builder;
        }
    }
}
