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

`BallEntity`'s tag set contains `ShapeEntity`'s as a subset. Single-group APIs that filter by `ShapeEntity`'s tags alone would match groups from both, with no way to pick one — so Trecs rejects the configuration at build instead of failing at every affected call site.

**Fix.** Register only the derived templates; the base is discovered automatically via `IExtends`. If you genuinely need both a base and a derived concrete group (e.g. `Orc` + `FlyingOrc`), give each a distinct discriminator tag so their tag sets are siblings, not strict subsets. See [Groups: `AddEntity` resolution](../advanced/groups-and-tagsets.md#addentity-which-group-does-the-entity-land-in).

## Mutating a set while iterating it

Using an immediate `Add` / `Remove` / `Clear` on the same set in the group you're currently iterating throws in DEBUG. In release the assertion is compiled out and iteration corrupts silently — entries get skipped, revisited, or (when an `Add` grows the buffer) read from freed memory.

**Fix.** Use the deferred set ops (the default) for changes-during-iteration, or stage them in a `NativeList<EntityHandle>` and apply after the loop. See [Sets — Immediate](../entity-management/sets.md#immediate).

## Forgetting to dispose pointers

Pointers must be manually disposed. DEBUG builds catch leaks at world shutdown and report them; release builds leak silently.

**Fix.** Dispose entity-owned pointers in an `OnRemoved` handler. See [Cleanup is manual for entity-owned pointers](../advanced/heap.md#cleanup-is-manual-for-entity-owned-pointers).

## Cleaning up an entity inline at the `Remove` call site

`Remove` is deferred — the entity stays in its groups until submission at end of fixed step. Any cleanup performed inline beside the `Remove` call (disposing pointers, releasing handles, zeroing fields) happens *before* the entity is actually gone. Systems ordered later in the same step still iterate the entity and read the torn state: disposed pointers, freed native memory, stale references.

```csharp
// ❌ Cleanup inline, then Remove.
[ForEachEntity(typeof(PatrolTags.Follower))]
void Execute(in Trail trail, EntityHandle entity)
{
    if (!ShouldRemove(entity)) return;
    trail.Value.Dispose(World);   // freed now
    entity.Remove(World);         // ...but entity stays in its group until submission
}

// A system ordered later in the same step still matches the entity
// and reads through the freed Trail pointer → crash or garbage.
```

**Fix.** Move the cleanup into an `OnRemoved` handler. Observer callbacks fire during submission, after every system's `Execute` for the step, so no system observes the half-cleaned entity. See [Entity Events](../entity-management/entity-events.md).

## Raw native collections in components don't round-trip through save/load

Component serialization copies the raw struct bytes (the blit fast-path). A `NativeList<T>` / `NativeHashMap<K,V>` value is a pointer to externally-allocated storage plus a length. Round-tripping such a component copies the bytes verbatim — the pointer that comes back on load is the previous session's memory address: no longer mapped, freed, or reassigned. Reading the deserialized collection crashes or returns garbage.

`NativeUniquePtr<NativeList<T>>` avoids the trap: the unique ptr is a heap-key, not a raw memory pointer, and Trecs's serializer walks the inner collection's contents through the heap rather than blitting the struct.

**Fix.** Wrap any native collection that needs to survive serialization in a `NativeUniquePtr` (or `NativeSharedPtr`). See [Storing native collections](../advanced/heap.md#storing-native-collections).

## `NativeUniquePtr<NativeList<T>>` — inner storage must be disposed first

The wrapped collection's storage is allocated in Unity's allocator, not Trecs's heap. Disposing the `NativeUniquePtr` only frees the heap slot holding the `NativeList` header — the underlying allocation leaks.

**Fix.** Dispose the inner collection, then the unique ptr. See [Storing native collections](../advanced/heap.md#storing-native-collections).

## Mutating a `NativeUniquePtr<T>` needs write access to the owning component

`Write(...)` on `NativeUniquePtr<T>` is a `ref this` instance method, so the call site needs a writeable reference to the pointer struct itself — typically a `.Write`-accessed component field. Calling it through a `ref readonly` (e.g. what `Component<T>(entity).Read` hands back) doesn't compile, because a `ref readonly` field isn't addressable as `ref`.

```csharp
// ❌ Won't compile: Read returns ref readonly, so the inner
//    NativeUniquePtr field isn't addressable as `ref this`.
ref readonly var buf = ref entity.Component<CScratchBuffer>(World).Read;
buf.List.Write(World).Value.Add(42);

// ✅ Take write access on the component.
ref var buf = ref entity.Component<CScratchBuffer>(World).Write;
buf.List.Write(World).Value.Add(42);
```

This is intentional: it lets the framework's component-level read/write tracking double as locking for the native data behind the pointer. Two systems writing the same component are already serialized; making `Write` on the pointer require component write access gets that serialization for free, with no per-pointer bookkeeping.

**Fix.** Get `.Write` on the owning component (or copy the pointer to a local) before calling `Write` on the pointer.

## Looking up a fresh `EntityHandle` in the same fixed step

`World.AddEntity<T>()` returns immediately with an `EntityInitializer` whose `Handle` is valid as an identity (stable, will resolve later) — but the entity isn't placed in any group until submission at end of the fixed step. Another system later in the same step that reads components on the handle throws, because the entity doesn't exist anywhere yet.

```csharp
// Fixed system A: spawn a child, store its handle on the parent
var childHandle = World.AddEntity<Child>().Set(...).Handle;
parent.ChildRef = childHandle;

// Fixed system B (same step, ordered later)
ref readonly var pos = ref parent.ChildRef.Component<Position>(World).Read;  // throws
```

**Fix.** Check `handle.Exists(World)` before dereferencing and skip if false — the handle becomes dereferenceable on the next step, once submission has run.

## Native heap allocations aren't visible to jobs in the same step

Native heap allocations (`NativeUniquePtr` / `NativeSharedPtr`) queue into a pending collection rather than the resolver lookup table Burst jobs read. The queue drains at submission (end of fixed step). A Burst job scheduled in the same step that calls `.Read(...)` / `.Write(...)` on a freshly-allocated native ptr through a resolver won't find it.

Main-thread `.Read(WorldAccessor)` / `.Write(WorldAccessor)` **do** work on freshly-allocated native ptrs — the main-thread path checks the pending queue before the resolver. Managed pointers (`UniquePtr<T>` / `SharedPtr<T>`) aren't deferred either; they have no resolver layer and are main-thread-only by design.

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

    public SharedPtr<ColorPalette> GetWarm() =>
        SharedPtr.Alloc<ColorPalette>(_world.Heap, AssetIds.WarmPalette);
}

// ✅ Service takes the accessor in; the calling Fixed system passes its own.
class PaletteService
{
    public SharedPtr<ColorPalette> GetWarm(WorldAccessor world) =>
        SharedPtr.Alloc<ColorPalette>(world.Heap, AssetIds.WarmPalette);
}
```

Variable-cadence phases (`EarlyPresentation` / `Presentation` / `LatePresentation`) and observer callbacks (`OnAdded` / `OnRemoved` / `OnMoved`) don't enforce this — services may use their own accessors there. Callbacks fire from inside `SubmitEntities`, which runs *between* Fixed-system executes rather than inside one.

**Fix.** Pass the calling system's `WorldAccessor` into the service rather than holding a separate one.

## Reading continuous time in fixed update with float-sensitive code

`DeltaTime` / `ElapsedTime` accumulate floating-point error that drifts across machines. For lockstep-deterministic workloads (RTS netcode etc.) this causes desync even when everything else is deterministic.

**Fix.** Set `WorldSettings.AssertNoTimeInFixedPhase = true` (throws on access during fixed phase) and use `World.FixedFrame` (a discrete tick counter) as your time source. Only applies to multiplayer games requiring cross-machine sync.

## Main-thread sync mid-phase stalls workers

Main-thread access through `WorldAccessor` (`.Read` / `.Write`) lazily completes conflicting in-flight jobs. Doing this mid-phase while jobs are running stops the job, idles workers, and tanks throughput.

**Fix.** Push main-thread reads/writes into a job, or order them after the job has had time to complete. See [Main-thread sync](../performance/dependency-tracking.md#main-thread-sync).
