# Gotchas

Common mistakes, edge cases, and surprises when building with Trecs. Each entry documents what goes wrong, why, and the fix. For the high-level rules, see [Best Practices](best-practices.md).

## `UnityEngine.Random` or `Time.deltaTime` in fixed update

Both vary across runs and break replay. The simulation desyncs from a recording the moment either is used during a fixed step.

**Fix.** Use `World.Rng` and `World.DeltaTime` (phase-aware). See [Time & RNG](../advanced/time-and-rng.md).

## Mutable state stored on a fixed-update system

System fields are not serialized. Anything mutable kept on a system silently diverges between record and replay.

**Fix.** Store dynamic state in components. Constructor parameters for immutable configuration are fine, as is caching data to members in OnReady.

## Registering both a base template and a template that extends it

```csharp
// ❌ World build throws: "Registered templates must not be base templates of other registered templates."
new WorldBuilder()
    .AddTemplate(ShapeEntity.Template)   // base
    .AddTemplate(BallEntity.Template)    // BallEntity : IExtends<ShapeEntity>, ...
    .BuildAndInitialize();
```

Every group of `BallEntity` contains `ShapeEntity`'s tag set as a subset. If both templates were registered, single-group APIs (`AddEntity<...>()`, `Warmup<...>()`, `[FromWorld(typeof(Tag))] GroupIndex` / `NativeEntitySetIndices<TSet>`) couldn't disambiguate a query expressed only in `ShapeEntity`'s tags — it would match groups from both. Trecs catches the configuration at build instead of failing later at every affected call site.

**Fix.** Register only the derived templates — base templates referenced via `IExtends` are discovered automatically and their components are folded into the derived groups. If your game truly needs both a `Shape` group and `Ball` groups concretely (e.g. `Orc` + `FlyingOrc`), give each a distinct discriminator tag (`ITagged<Grounded>` on the base, `ITagged<Flying>` on the derived) so their tag sets are siblings rather than strict subsets. See [Groups: `AddEntity` resolution](../advanced/groups-and-tagsets.md#addentity-which-group-does-the-entity-land-in).

## Mutating a set while iterating it

Using an immediate `Add` / `Remove` / `Clear` on the same set in the group you're currently iterating throws in DEBUG. In release the assertion is compiled out and iteration corrupts silently — entries get skipped, revisited, or (when an `Add` grows the buffer) read from freed memory.

**Fix.** Use the deferred set ops (the default) for changes-during-iteration, or stage them in a `NativeList<EntityHandle>` and apply after the loop. See [Sets — Immediate](../entity-management/sets.md#immediate).

## Forgetting to dispose pointers

Pointers must be manually disposed. DEBUG builds catch leaks at world shutdown and report them; release builds leak silently.

**Fix.** Dispose entity-owned pointers in an `OnRemoved` handler. See [Cleanup is manual for entity-owned pointers](../advanced/heap.md#cleanup-is-manual-for-entity-owned-pointers).

## Raw native collections in components don't round-trip through save/load

Component serialization copies the raw struct bytes (the blit fast-path). A `NativeList<T>` / `NativeHashMap<K,V>` value is a pointer to externally-allocated storage plus a length. Round-tripping such a component copies the bytes verbatim — the pointer that comes back on load is the previous session's memory address: no longer mapped, freed, or reassigned. Reading the deserialized collection crashes or returns garbage.

`NativeUniquePtr<NativeList<T>>` avoids the trap: the unique ptr is a heap-key, not a raw memory pointer, and Trecs's serializer walks the inner collection's contents through the heap rather than blitting the struct.

**Fix.** Wrap any native collection that needs to survive serialization in a `NativeUniquePtr` (or `NativeSharedPtr`). See [Wrapping native collections](../advanced/heap.md#wrapping-native-collections).

## `NativeUniquePtr<NativeList<T>>` — inner storage must be disposed first

The wrapped collection's storage is allocated in Unity's allocator, not Trecs's heap. Disposing the `NativeUniquePtr` only frees the heap slot holding the `NativeList` header — the underlying allocation leaks.

**Fix.** Dispose the inner collection, then the unique ptr. See [Wrapping native collections](../advanced/heap.md#wrapping-native-collections).

## Mutating a `NativeUniquePtr<T>` needs write access to the owning component

`GetMut` and `Set` on `NativeUniquePtr<T>` are defined as `ref this` extension methods, so the call site needs a writeable reference to the pointer struct itself — typically a `Write`-accessed component field. Calling them through a `ref readonly` (e.g. what `Component<T>(entity).Read` hands back) doesn't compile, because a `ref readonly` field isn't addressable as `ref`.

```csharp
// ❌ Won't compile: Read returns ref readonly, so the inner
//    NativeUniquePtr field isn't addressable as `ref this`.
ref readonly var buf = ref World.Component<CScratchBuffer>(entity).Read;
buf.List.GetMut(World).Add(42);

// ✅ Take write access on the component.
ref var buf = ref World.Component<CScratchBuffer>(entity).Write;
buf.List.GetMut(World).Add(42);
```

This is intentional: it lets mutations to the pointed-to native data piggy-back on the framework's existing component resource tracking. The scheduler already knows which systems read and write each component, so requiring write access on the component in order to mutate the native allocation behind the pointer means cross-system contention on that allocation is automatically serialized — no separate per-pointer bookkeeping is needed. Read-only access (`Get`) has no such requirement and works through a `ref readonly` component, which similarly composes with the read-side of component tracking.

**Fix.** Get `.Write` on the owning component (or copy the pointer to a local) before calling `GetMut` / `Set`.

## Looking up a fresh `EntityHandle` in the same fixed step

`World.AddEntity<T>()` returns immediately with an `EntityInitializer` whose `Handle` is valid as an identity (stable, will resolve later) — but the entity isn't placed in any group until submission at end of the fixed step. Another system later in the same step that reads components on the handle throws, because the entity doesn't exist anywhere yet.

```csharp
// Fixed system A: spawn a child, store its handle on the parent
var childHandle = World.AddEntity<Child>().Set(...).Handle;
parent.ChildRef = childHandle;

// Fixed system B (same step, ordered later)
ref readonly var pos = ref World.Component<Position>(parent.ChildRef).Read;  // throws
```

**Fix.** Check `World.EntityExists(handle)` before dereferencing and skip if false — the handle becomes dereferenceable on the next step, once submission has run.

## Native heap allocations aren't visible to jobs in the same step

Native heap allocations (`NativeUniquePtr` / `NativeSharedPtr`) queue into a pending collection rather than the resolver lookup table Burst jobs read. The queue drains at submission (end of fixed step). A Burst job scheduled in the same step that calls `.Get(NativeWorldAccessor)` on a freshly-allocated native ptr won't find it.

Main-thread `.Get(WorldAccessor)` / `.Set(WorldAccessor, ...)` **do** work on freshly-allocated native ptrs — the main-thread path checks the pending queue before the resolver. Managed pointers (`UniquePtr<T>` / `SharedPtr<T>`) aren't deferred either; they have no resolver layer and are main-thread-only by design.

The deferral exists because Burst jobs hold a snapshot of the resolver's allocation table; mutating it mid-job would corrupt in-flight reads.

**Fix.** Wait 1 frame, or if the work doesn't have to be Burst, do it from the main thread instead.

## Just-spawned entities haven't been fixed-updated when Presentation sees them

An entity spawned in a Fixed system is submitted in time for the same Tick's Presentation, but no fixed-update cycle has run on it yet — Presentation sees the spawn-time initial values, not the post-tick values. The entity renders at its initial state for one frame, then jumps to the correct state on the next tick. Visible as a brief stutter or wrong-position pop on spawn.

**Fix.** Initialize *everything Presentation reads* at spawn time. If impractical, use an enabled flag defaulting to false, then flip it during fixed update.

## Service-class accessor used during a Fixed system's `Execute`

During a `Fixed`-role system's `Execute`, only that system's own accessor may touch ECS state. Other accessors — including another `Fixed`-role one held by a service, or an `Unrestricted` one — throw if used mid-Fixed-execute. Recording access under the service's `DebugName` instead of the calling system's scrambles debug attribution, and `Unrestricted` accessors risk smuggling non-deterministic state into the simulation.

```csharp
// ❌ Service holds its own accessor; trips the strict-accessor rule
//    when called from a Fixed system's Execute.
class PaletteService
{
    WorldAccessor _world;

    public PaletteService(World world)
    {
        _world = world.CreateAccessor(AccessorRole.Fixed);
    }

    public SharedPtr<ColorPalette> GetWarm() => _world.Heap.AllocShared<ColorPalette>(AssetIds.WarmPalette);
}

// ✅ Service takes the accessor in; the calling Fixed system passes its own.
class PaletteService
{
    public SharedPtr<ColorPalette> GetWarm(WorldAccessor world) =>
        world.Heap.AllocShared<ColorPalette>(AssetIds.WarmPalette);
}
```

Variable-cadence phases (`EarlyPresentation` / `Presentation` / `LatePresentation`) and observer callbacks (`OnAdded` / `OnRemoved` / `OnMoved`) don't enforce this — services may use their own accessors there. Callbacks fire from inside `SubmitEntities`, which runs *between* Fixed-system executes rather than inside one.

**Fix.** Pass the calling system's `WorldAccessor` into the service rather than holding a separate one.

## Reading continuous time in fixed update with float-sensitive code

`DeltaTime` / `ElapsedTime` accumulate floating-point error that drifts across machines. For lockstep-deterministic workloads (RTS netcode etc.) this causes desync even when everything else is deterministic.

**Fix.** Set `WorldSettings.AssertNoTimeInFixedPhase = true` (throws on access during fixed phase) and use `World.FixedFrame` (a discrete tick counter) as your time source. See [`AssertNoTimeInFixedPhase`](../advanced/serialization.md#worldsettingsassertnotimeinfixedphase). Only applies to multiplayer games requiring cross-machine sync.

## Main-thread sync mid-phase stalls workers

Main-thread access through `WorldAccessor` (`.Read` / `.Write`) lazily completes conflicting in-flight jobs. Doing this mid-phase while jobs are running stops the job, idles workers, and tanks throughput.

**Fix.** Push main-thread reads/writes into a job, or order them after the job has had time to complete. See [Main-thread sync](../performance/dependency-tracking.md#main-thread-sync).
