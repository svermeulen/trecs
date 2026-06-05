# World Setup

Every Trecs application starts by building a `World` — the container that owns all entities and components and drives the per-frame system update.

## WorldBuilder

```csharp
var world = new WorldBuilder()
    .SetSettings(new WorldSettings
    {
        FixedTimeStep = 1f / 60f,
        RandomSeed = 42,
    })
    .AddTemplate(SampleTemplates.SpinnerEntity.Template)
    .AddSystem(new SpinnerSystem(rotationSpeed: 2f))
    .BuildAndInitialize();
```

### Builder methods

| Method | Description |
|--------|-------------|
| `SetDebugName(string)` | Human-readable name (surfaces in editor tooling) |
| `SetSettings(WorldSettings)` | Configure timing, determinism, debug options — see [WorldSettings](#worldsettings) |
| `AddTemplate(Template)` / `AddTemplates(...)` | Register entity [templates](templates.md). Each contributes one or more [groups](../advanced/groups-and-tagsets.md#groups). |
| `AddSet<T>()` | Register an [entity set](../entity-management/sets.md) |
| `SetBlobCacheSettings(BlobCacheSettings)` | Tune the shared [heap](../experimental/pointers.md) blob cache (inactive-memory caps and eviction). Optional — sensible defaults apply if omitted. |
| `AddSystem(ISystem)` / `AddSystems(...)` | Register [systems](systems.md) |
| `AddSystemOrderConstraint(params Type[])` | Declare [system ordering](systems.md#system-ordering) |
| `RegisterSerializer(ISerializer)` / `RegisterSerializer<T>()` | Register a custom [serializer](../experimental/serialization.md) for save/load |
| `RegisterComponentArraySerializer<T>(...)` | Override per-component-array serialization |
| `SetPoolManager(ITrecsPoolManager)` | Custom pool manager for internal allocations |
| `Build()` | Build the world (returns `World`) |
| `BuildAndInitialize()` | Build and immediately initialize |

### Adding systems

Systems can be registered on the builder *or* on the world directly — both are equivalent:

```csharp
// On the builder
new WorldBuilder()
    .AddSystem(new PhysicsSystem())
    .AddSystem(new RenderSystem(gameObjectRegistry))
    .BuildAndInitialize();

// Or after Build() — useful when system constructors need a live World
var world = new WorldBuilder().Build();
world.AddSystem(new PhysicsSystem());
world.AddSystem(new RenderSystem(gameObjectRegistry));
world.Initialize();
```

Both `AddSystem` calls must happen before `Initialize()`.

## WorldSettings

```csharp
// Defaults shown for reference. Only set what you want to change.
var settings = new WorldSettings
{
    // Timing
    FixedTimeStep = 1f / 60f,
    MaxSecondsForFixedUpdatePerFrame = 1f / 3f, // Cap on fixed-update time per frame; null = uncapped

    // Determinism
    RandomSeed = null,                          // ulong? — null seeds from System.Environment.TickCount

    // Startup
    StartPaused = false,

    // Debug warnings
    WarnOnFixedUpdateFallingBehind = true,
    WarnOnJobSyncPoints = false,
    WarnOnUnusedTemplates = false,

    // Safety
    MaxSubmissionIterations = 10,               // Prevent circular submission feedback
    AssertNoTimeInFixedPhase = false,           // true → time properties throw in fixed phase; use FixedFrame as a discrete tick counter instead

    // Logging
    MinLogLevel = LogLevel.Warning,             // Minimum severity for Trecs log messages
};
```

## World lifecycle

```csharp
// 1. Build
var world = new WorldBuilder()
    .AddTemplate(...)
    .Build();

// 2. (Optional) Add systems
world.AddSystem(new FooSystem());

// 3. Initialize — allocates storage, locks system list, builds the global
//    entity, then runs OnReady hooks
world.Initialize();

// 4. (Optional) Standalone WorldAccessor for non-system code
var accessor = world.CreateAccessor(AccessorRole.Unrestricted);

// 5. Game loop
while (running)
{
    world.Tick();       // EarlyPresentation → Input + Fixed catch-up → Presentation
    world.LateTick();   // LatePresentation
}

// 6. Cleanup — removes all entities, runs OnShutdown hooks (reverse of OnReady),
//    then tears down state
world.Dispose();
```

`BuildAndInitialize()` combines steps 1 and 3. Use it when no systems need to be added post-`Build()`.

For the per-frame breakdown of what `Tick()` and `LateTick()` run, see [the phase diagram](systems.md#phase-diagram).

### Disposal

`World.Dispose()` tears the world down in a fixed order:

1. **Enter the shutdown guard** (see below).
2. **[`RemoveAllEntities`](../entity-management/structural-changes.md#removing-entities-in-bulk) + submit** — removes every non-global entity through the normal deferred pipeline, firing one last `OnRemoved` for each under the usual [callback contract](../entity-management/entity-events.md#the-onremoved-contract). This is what guarantees the final `OnRemoved` cleanup pass.
3. **System `OnShutdown` hooks** — in reverse `OnReady` order (see [OnShutdown](systems.md#onshutdown-hook)).
4. **The [`OnShutdown` frame event](../entity-management/entity-events.md#frame-events)** — where non-system code disposes its event subscriptions.
5. **Infrastructure teardown** — heaps, component store, input queue, etc.

The global singleton entity is never removed in step 2, so `World.GlobalComponent<T>()` stays valid through `OnShutdown`.

#### The shutdown guard

From the moment `Dispose()` begins (step 1), structural changes from any callback that runs during shutdown — an `OnRemoved` handler, a system `OnShutdown`, or the `OnShutdown` frame event — are gated:

- **Adds are rejected.** A new entity added during shutdown could never receive a matching `OnRemoved`, so `AddEntity` **throws in debug builds** and is a **no-op in release** (the entity never materializes, fires no `OnAdded`). Do cleanup in shutdown callbacks, not entity creation.
- **Per-entity removes are ignored** — every group is already being removed, so they would be redundant.
- **Global component writes are allowed** — the global entity is still alive, so flushing final state onto it from `OnShutdown` works.

## WorldAccessor

`WorldAccessor` is the primary runtime API. Systems get one via source generation. For non-system code (init, lifecycle hooks, debug tooling, event callbacks) create one manually:

```csharp
var accessor = world.CreateAccessor(AccessorRole.Unrestricted);
```

`AccessorRole` controls which operations the accessor may perform — see [Accessor Roles](../advanced/accessor-roles.md) for the full matrix.

`WorldAccessor` exposes:

- **[Entity operations](../entity-management/structural-changes.md)** — `AddEntity`, `RemoveEntity`, `SetTag` / `UnsetTag` (partition transitions)
- **[Component access](components.md)** — `GlobalComponent<T>`, `ComponentBuffer<T>` (per-entity access is via `EntityHandle.Component<T>(WorldAccessor)` — see [Components](components.md))
- **[Queries](../data-access/queries-and-iteration.md)** — `Query()`, `CountEntitiesWithTags<T>()`
- **[Sets](../entity-management/sets.md)** — `Set<T>()` gateway with `DeferredAdd` / `DeferredRemove` / `DeferredClear`, `Read`, and `Write` views
- **[Events](../entity-management/entity-events.md)** — `Events` builder for entity lifecycle subscriptions
- **[Time](../advanced/time-and-rng.md)** — `DeltaTime`, `ElapsedTime`, `Frame` (phase-aware)
- **[RNG](../advanced/time-and-rng.md)** — `Rng`, `FixedRng`, `VariableRng`
- **[Heap](../experimental/pointers.md)** — shared/unique pointer allocation APIs (`SharedPtr`, `UniquePtr`, and their native variants) for data that lives outside the component buffer
- **[Jobs](../performance/jobs-and-burst.md)** — `ToNative()` for job-safe access
- **Global entity** — `GlobalEntityHandle` returns the `EntityHandle` for the world's singleton global entity, and `GlobalComponent<T>()` gives direct component access to it
- **System control** — `SetSystemEnabled` / `SetSystemPaused` / `IsSystemEffectivelyEnabled` for toggling systems at runtime
- **Metadata** — `WorldInfo` for template and storage introspection
