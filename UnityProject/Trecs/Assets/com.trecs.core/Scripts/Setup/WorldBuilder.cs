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
    public sealed class WorldBuilder
    {
        readonly List<ISystem> _systems = new();
        readonly List<IBlobStore> _blobStores = new();
        readonly List<SystemOrderConstraint> _systemOrderConstraints = new();

        internal readonly List<IInterpolatedPreviousSaver> _interpolatedPreviousSavers = new();
        readonly List<Template> _templates = new();
        readonly List<EntitySet> _sets = new();
        internal ISystemEntryProvider _systemEntryProvider;
        readonly SerializerRegistry _serializerRegistry;
        readonly ComponentArraySerializerRegistry _componentArraySerializerRegistry = new();

        bool _hasBuilt;
        BlobCacheSettings _blobCacheSettings;
        WorldSettings _settings;
        ITrecsPoolManager _poolManager;
        string _debugName;

        /// <summary>
        /// Creates a new WorldBuilder. Pass an externally-owned
        /// <see cref="SerializerRegistry"/> when the registry must live in
        /// the DI container and be resolvable before the world exists
        /// (e.g. installers that register custom serializers at install
        /// time). The caller is responsible for pre-populating defaults
        /// on a supplied registry; when <paramref name="serializerRegistry"/>
        /// is null the builder creates one and auto-populates it with the
        /// common trecs serializers.
        /// </summary>
        public WorldBuilder(SerializerRegistry serializerRegistry = null)
        {
            if (serializerRegistry == null)
            {
                _serializerRegistry = new SerializerRegistry();
                DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_serializerRegistry);
            }
            else
            {
                _serializerRegistry = serializerRegistry;
            }
        }

        /// <summary>
        /// Sets a human-readable debug name on the resulting <see cref="World.DebugName"/>.
        /// Surfaced by editor tooling (e.g. the World dropdown in <c>TrecsPlayerWindow</c>).
        /// </summary>
        public WorldBuilder SetDebugName(string debugName)
        {
            TrecsAssert.That(debugName != null, "debugName must not be null");
            TrecsAssert.That(_debugName == null, "DebugName has already been set");
            _debugName = debugName;
            return this;
        }

        /// <summary>
        /// Sets the <see cref="WorldSettings"/> for the world being built.
        /// </summary>
        public WorldBuilder SetSettings(WorldSettings settings)
        {
            TrecsAssert.That(settings != null, "settings must not be null");
            TrecsAssert.That(_settings == null, "Settings have already been set");
            _settings = settings;
            return this;
        }

        /// <summary>
        /// Registers a concrete template with the world. Each concrete template
        /// determines a group (contiguous memory layout) for its component
        /// arrays. Only register templates that will be instantiated
        /// directly — base templates used via <c>IExtends</c> are discovered
        /// automatically and should not be registered here.
        /// </summary>
        public WorldBuilder AddTemplate(Template template)
        {
            TrecsAssert.That(template != null, "template must not be null");
            TrecsAssert.That(
                !template.IsAbstract,
                "Template '{0}' is marked abstract — abstract templates may only be used as IExtends<> bases. Remove the 'abstract' keyword or register a concrete derived template instead.",
                template.DebugName
            );
            _templates.Add(template);
            return this;
        }

        /// <summary>
        /// Registers an entity set with the world.
        /// </summary>
        public WorldBuilder AddSet<T>()
            where T : struct, IEntitySet
        {
            // EntitySet<T>.Value's cctor runs SetFactory.CreateSet, which populates the
            // registries that SetId<T>.Value (and everything downstream) depends on.
            var entitySet = EntitySet<T>.Value;

            TrecsAssert.That(
                !_sets.Any(f => f.Id == entitySet.Id),
                "Set '{0}' is already added to the WorldBuilder",
                entitySet.DebugName
            );
            _sets.Add(entitySet);

            return this;
        }

        /// <summary>
        /// Registers multiple concrete templates with the world.
        /// </summary>
        public WorldBuilder AddTemplates(IEnumerable<Template> templates)
        {
            TrecsAssert.That(templates != null, "templates must not be null");
            foreach (var template in templates)
            {
                AddTemplate(template);
            }
            return this;
        }

        /// <summary>
        /// Sets a custom pool manager for internal memory allocation.
        /// </summary>
        public WorldBuilder SetPoolManager(ITrecsPoolManager poolManager)
        {
            TrecsAssert.That(poolManager != null, "poolManager must not be null");
            TrecsAssert.That(_poolManager == null, "PoolManager has already been set");
            _poolManager = poolManager;
            return this;
        }

        /// <summary>
        /// Adds a system to the world.
        /// </summary>
        public WorldBuilder AddSystem(ISystem system)
        {
            TrecsAssert.That(system != null, "system must not be null");
            _systems.Add(system);
            return this;
        }

        /// <summary>
        /// Adds multiple systems to the world.
        /// </summary>
        public WorldBuilder AddSystems(IEnumerable<ISystem> systems)
        {
            TrecsAssert.That(systems != null, "systems must not be null");
            foreach (var system in systems)
            {
                AddSystem(system);
            }
            return this;
        }

        /// <summary>
        /// Adds a blob store for loading shared blob data. Pass an instance of one of the
        /// supplied store types — <see cref="BlobStoreInMemory"/>, or the Svkj-package
        /// <c>BlobStoreFiles</c> / <c>BlobStoreAddressables</c>. The <see cref="IBlobStore"/>
        /// contract is not intended for external implementation.
        ///
        /// If no blob store is added before <see cref="Build"/>, the builder falls back to a
        /// <see cref="BlobStoreInMemory"/> with <see cref="BlobStoreInMemorySettings.Default"/>.
        /// </summary>
        public WorldBuilder AddBlobStore(IBlobStore store)
        {
            TrecsAssert.That(store != null, "store must not be null");
            _blobStores.Add(store);
            return this;
        }

        /// <summary>
        /// Adds multiple blob stores for loading shared blob data. See <see cref="AddBlobStore"/>.
        /// </summary>
        public WorldBuilder AddBlobStores(IEnumerable<IBlobStore> stores)
        {
            TrecsAssert.That(stores != null, "stores must not be null");
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
            TrecsAssert.That(settings != null, "settings must not be null");
            TrecsAssert.That(_blobCacheSettings == null, "BlobCacheSettings have already been set");
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
            TrecsAssert.That(constraints != null, "constraints must not be null");
            foreach (var constraint in constraints)
            {
                AddSystemOrderConstraint(constraint);
            }
            return this;
        }

        /// <summary>
        /// Registers a custom serializer instance. The serializer must
        /// implement <see cref="ISerializer{T}"/>; the handled object type
        /// is inferred from that interface. Instance registration is
        /// exclusive — throws if the target object type already has a
        /// serializer registered (instance or Type-based).
        /// </summary>
        public WorldBuilder RegisterSerializer(ISerializer serializer)
        {
            _serializerRegistry.RegisterSerializer(serializer);
            return this;
        }

        /// <summary>
        /// Registers a serializer by Type. The target object type is read
        /// via reflection at registration time, but the serializer is
        /// lazily constructed via its parameterless constructor on first
        /// lookup and cached. The same serializer Type may be registered
        /// from multiple call sites — extra registrations are silently
        /// ignored. Different serializer Types targeting the same object
        /// type still throw.
        /// </summary>
        public WorldBuilder RegisterSerializer<TSerializer>()
            where TSerializer : ISerializer, new()
        {
            _serializerRegistry.RegisterSerializer<TSerializer>();
            return this;
        }

        /// <inheritdoc cref="RegisterSerializer{TSerializer}"/>
        public WorldBuilder RegisterSerializer(Type serializerType)
        {
            _serializerRegistry.RegisterSerializer(serializerType);
            return this;
        }

        /// <summary>
        /// Registers an <see cref="IComponentArraySerializer{T}"/> for component
        /// type <typeparamref name="T"/>. Equivalent to calling
        /// <c>world.ComponentArraySerializerRegistry.Register(serializer)</c>
        /// post-build, but available up front for callers that pre-configure
        /// everything via the builder.
        /// </summary>
        public WorldBuilder RegisterComponentArraySerializer<T>(
            IComponentArraySerializer<T> serializer
        )
            where T : unmanaged, IEntityComponent
        {
            _componentArraySerializerRegistry.Register(serializer);
            return this;
        }

#if DEBUG && !TRECS_IS_PROFILING
        void Validate()
        {
            // Check for duplicate templates
            var seenTemplates = new HashSet<Template>();
            foreach (var template in _templates)
            {
                TrecsDebugAssert.That(
                    seenTemplates.Add(template),
                    "Duplicate template '{0}' added to WorldBuilder",
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
            TrecsAssert.That(!_hasBuilt, "Build() has already been called");
            _hasBuilt = true;

#if DEBUG && !TRECS_IS_PROFILING
            Validate();
#endif

            var settings = _settings ?? new WorldSettings();

            // One TrecsLog instance per World — every framework class caches this same
            // reference. WorldAccessor.Log exposes it to user systems.
            var log = new TrecsLog(settings);

            var worldInfo = new WorldInfo(log, _templates, _sets);

            var uniqueHeap = new UniqueHeap(log, _poolManager);
            var nativeBlobBoxPool = new NativeBlobBoxPool();

            // If no blob stores were registered, fall back to an in-memory store with default
            // settings so heap operations work out of the box. Callers that added at least one
            // store (even a read-only one like BlobStoreAddressables) are assumed to have
            // configured things intentionally, so we leave their list untouched.
            if (_blobStores.Count == 0)
            {
                _blobStores.Add(new BlobStoreInMemory(BlobStoreInMemorySettings.Default));
            }

            var blobCache = new BlobCache(log, _blobStores, _blobCacheSettings, nativeBlobBoxPool);
            var sharedHeap = new SharedHeap(log, blobCache);
            var nativeSharedHeap = new NativeSharedHeap(log, blobCache);
            var nativeUniqueChunkStore = new NativeHeap(log);
            var inputNativeUniqueHeap = new InputNativeUniqueHeap(log);
            var inputNativeSharedHeap = new InputNativeSharedHeap(log, blobCache);
            var inputSharedHeap = new InputSharedHeap(log, blobCache);
            var inputUniqueHeap = new InputUniqueHeap(log, _poolManager);

            var accessorRegistry = new WorldAccessorRegistry(log);

            _systemEntryProvider ??= new DefaultSystemEntryProvider(
                log,
                _systemOrderConstraints,
                accessorRegistry
            );

            var systemLoader = new SystemLoader(log, _systemEntryProvider);

            var eventsManager = new EventsManager(log);

            var componentStore = new ComponentStore(log, worldInfo.AllGroups.Count);
            var setStore = new SetStore(worldInfo.AllGroups.Count);

            foreach (var entitySet in _sets)
            {
                setStore.RegisterSet(entitySet, worldInfo);
            }

            var entityQuerier = new EntityQuerier(
                log,
                componentStore,
                setStore,
                worldInfo.AllGroups.Count
            );

            var jobScheduler = new RuntimeJobScheduler();

            var submitter = new EntitySubmitter(
                log,
                worldInfo,
                accessorRegistry,
                eventsManager,
                componentStore,
                setStore,
                settings,
                entityQuerier,
                nativeSharedHeap,
                inputNativeSharedHeap,
                nativeUniqueChunkStore,
                jobScheduler
            );

            var interpolatedPreviousSaverManager = new InterpolatedPreviousSaverManager(
                _interpolatedPreviousSavers
            );

            var entityInputQueue = new EntityInputQueue(
                log,
                inputSharedHeap,
                inputNativeSharedHeap,
                inputUniqueHeap,
                inputNativeUniqueHeap,
                worldInfo
            );

            var systemRunner = new SystemRunner(
                log,
                submitter,
                settings,
                interpolatedPreviousSaverManager,
                jobScheduler,
                entityInputQueue
            );

            var world = new World(
                log: log,
                entityInputQueue: entityInputQueue,
                systemRunner: systemRunner,
                uniqueHeap: uniqueHeap,
                nativeUniqueChunkStore: nativeUniqueChunkStore,
                inputNativeUniqueHeap: inputNativeUniqueHeap,
                inputNativeSharedHeap: inputNativeSharedHeap,
                inputSharedHeap: inputSharedHeap,
                inputUniqueHeap: inputUniqueHeap,
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
                nativeBlobBoxPool: nativeBlobBoxPool,
                interpolatedPreviousSaverManager: interpolatedPreviousSaverManager,
                componentStore: componentStore,
                systems: _systems,
                serializerRegistry: _serializerRegistry,
                componentArraySerializerRegistry: _componentArraySerializerRegistry
            );
            world.DebugName = _debugName;

            return world;
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
        public static WorldBuilder AddInterpolatedPreviousSaver<T>(
            this WorldBuilder builder,
            InterpolatedPreviousSaver<T> saver
        )
            where T : unmanaged, IEntityComponent
        {
            builder._interpolatedPreviousSavers.Add(saver);
            return builder;
        }

        /// <summary>
        /// Overrides the default system entry provider used for system ordering and accessor resolution.
        /// </summary>
        public static WorldBuilder SetSystemEntryProvider(
            this WorldBuilder builder,
            ISystemEntryProvider systemEntryProvider
        )
        {
            TrecsAssert.That(systemEntryProvider != null, "systemEntryProvider must not be null");
            builder._systemEntryProvider = systemEntryProvider;
            return builder;
        }
    }
}
