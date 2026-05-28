# Gotchas

Common mistakes, edge cases, and surprises when building with Trecs. Each entry documents what goes wrong, why, and the fix. For the high-level rules, see [Best Practices](best-practices.md).

## `UnityEngine.Random` or `Time.deltaTime` in fixed update

Both vary across runs and break replay. The simulation desyncs from a recording the moment either is used during a fixed step.

**Fix.** Use `World.Rng` and `World.DeltaTime` (phase-aware). See [Time & RNG](../advanced/time-and-rng.md).

## Mutable state stored on a fixed-update system

System fields are not serialized. Anything mutable kept on a system silently diverges between record and replay.

**Fix.** Store dynamic state in components. Constructor parameters for immutable configuration are fine, as is caching data to members in OnReady.

## Mutating a set while iterating it

Using an immediate `Add` / `Remove` / `Clear` on the same set you're currently iterating throws exceptions in debug/editor builds. In release the assertion is compiled out, so iteration corrupts silently (entries get skipped, revisited, or read from freed memory).

**Fix.** Use the deferred set ops for changes-during-iteration, or stage them in a `NativeList<EntityHandle>` and apply after the loop. See [Sets — Immediate](../entity-management/sets.md#immediate).

## Forgetting to dispose pointers

Unique and Shared Pointers must be manually disposed. Debug/Editor builds catch leaks at world shutdown and report them; release builds leak silently.

**Fix.** Dispose entity-owned pointers in an `OnRemoved` handler. See [Cleanup is manual for entity-owned pointers](../experimental/pointers.md#cleanup-is-manual-for-entity-owned-pointers).

## Cleaning up an entity inline at the `Remove` call site

`Remove` is deferred — the entity stays in storage until submission at end of fixed step. Any cleanup performed inline beside the `Remove` call (disposing pointers, releasing handles, zeroing fields) happens *before* the entity is actually gone. Systems ordered later in the same step still iterate the entity and read the torn state.

```csharp
// ❌ Cleanup inline, then Remove.
[ForEachEntity(typeof(PatrolTags.Follower))]
void Execute(in Trail trail, EntityHandle entity)
{
    if (!ShouldRemove(entity)) return;
    trail.Value.Dispose(World);   // freed now
    entity.Remove(World);         // ...but entity stays in storage until submission
}

// A system ordered later in the same step still matches the entity
// and reads through the freed Trail pointer → crash or garbage.
```

**Fix.** Move the cleanup into an `OnRemoved` handler. Observer callbacks fire during submission, after every system's `Execute` for the step, so no system observes the half-cleaned entity. See [Entity Events](../entity-management/entity-events.md).

## Raw native collections in components don't round-trip through save/load

Component serialization copies the raw struct bytes (the blit fast-path). A `NativeList<T>` / `NativeHashMap<K,V>` value is a pointer to externally-allocated storage plus a length. Round-tripping such a component copies the bytes verbatim — the pointer that comes back on load is the previous session's memory address: no longer mapped, freed, or reassigned. Reading the deserialized collection crashes or returns garbage.

**Fix.** See [Dynamic Collections](../experimental/dynamic-collections.md).

## Mutating a `NativeUniquePtr<T>` needs write access to the owning component

```csharp
// ❌ Won't compile: Read returns ref readonly, so the inner
//    NativeUniquePtr field isn't addressable as `ref this`.
ref readonly var buf = ref entity.Component<CScratchBuffer>(World).Read;
buf.List.Write(World).Value.Add(42);

// ✅ Take write access on the component.
ref var buf = ref entity.Component<CScratchBuffer>(World).Write;
buf.List.Write(World).Value.Add(42);
```

This is intentional: it lets the framework's component-level read/write tracking double as locking for the native data behind the pointer.

**Fix.** Get `.Write` on the owning component (or copy the pointer to a local) before calling `Write` on the pointer.

## Looking up a fresh `EntityHandle` in the same fixed step

`World.AddEntity<T>()` returns an `EntityInitializer` whose `.Handle` property gives you an `EntityHandle` immediately — but the entity isn't actually created until the next submission. So if this `EntityHandle` is stored in a component, and then another system later in the same step attempts to read via this `EntityHandle`, the lookup fails.

```csharp
// Fixed system A: spawn a child, store its handle on the parent
var childHandle = World.AddEntity<Child>().Set(...).Handle;
parent.ChildRef = childHandle;

// Fixed system B (same step, ordered later)
ref readonly var pos = ref parent.ChildRef.Component<Position>(World).Read;  // throws
```

**Fix.** Check `handle.Exists(World)` before dereferencing and skip if false — the handle becomes dereferenceable on the next step, once submission has run.


## Just-spawned entities haven't been fixed-updated when Presentation sees them

An entity spawned in a Fixed system is submitted in time for the same Tick's Presentation phase, but no fixed-update cycle has run on it yet — Presentation sees the spawn-time initial values, not the post-tick values. The entity renders at its initial state for one frame, then jumps to the correct state on the next tick. Visible as a brief stutter or wrong-position pop on spawn.

**Fix.** Initialize *everything Presentation reads* at spawn time. If impractical, use an enabled flag defaulting to false, then flip it during fixed update.

## Service-class accessor used during a Fixed system's `Execute`

During a `Fixed`-role system's `Execute`, only that system's own accessor may touch ECS state. Other accessors — including another `Fixed`-role one held by a service, or even an `Unrestricted` one — throw if used mid-Fixed-execute. Recording access under the service's `DebugName` instead of the calling system's scrambles debug attribution, and `Unrestricted` accessors risk smuggling non-deterministic state into the simulation.

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
        SharedPtr.Acquire<ColorPalette>(_world, AssetIds.WarmPalette);
}

// ✅ Service takes the accessor in; the calling Fixed system passes its own.
class PaletteService
{
    public SharedPtr<ColorPalette> GetWarm(WorldAccessor world) =>
        SharedPtr.Acquire<ColorPalette>(world, AssetIds.WarmPalette);
}
```

Variable-cadence phases (`EarlyPresentation` / `Presentation` / `LatePresentation`) and observer callbacks (`OnAdded` / `OnRemoved` / `OnMoved`) don't enforce this — services may use their own accessors there. Callbacks fire from inside `Submit`, which runs *between* Fixed-system executes rather than inside one.

**Fix.** Pass the calling system's `WorldAccessor` into the service rather than holding a separate one.

## Reading continuous time in fixed update with float-sensitive code

`DeltaTime` / `ElapsedTime` accumulate floating-point error that drifts across machines. For lockstep-deterministic workloads (RTS netcode etc.) this causes desync even when everything else is deterministic.

**Fix.** Set `WorldSettings.AssertNoTimeInFixedPhase = true` (throws on access during fixed phase) and use `World.FixedFrame` (a discrete tick counter) as your time source. Only applies to multiplayer games requiring cross-machine sync.

## Main-thread sync mid-phase stalls workers

Main-thread access through `WorldAccessor` (`.Read` / `.Write`) lazily completes conflicting in-flight jobs. Doing this mid-phase while jobs are running stops the job, idles workers, and tanks throughput.

**Fix.** Push main-thread reads/writes into a job, or order them after the job has had time to complete. See [Main-thread sync](../performance/dependency-tracking.md#main-thread-sync).
