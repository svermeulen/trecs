# Gotchas

Common mistakes, edge cases, and surprises when building with Trecs. Each entry documents what goes wrong, why, and the fix. For the high-level rules, see [Best Practices](best-practices.md).

## `UnityEngine.Random` or `Time.deltaTime` in fixed update

Both vary across runs and break replay. The simulation desyncs from a recording the moment one of these are used during a fixed step.

**Fix.** Use `World.Rng` and `World.DeltaTime` (phase-aware). See [Time & RNG](../advanced/time-and-rng.md).

## Mutable state stored on a fixed-update system

System fields are not serialized. Anything mutable kept on a system silently diverges between record and replay.

**Fix.** Store dynamic state in components. Constructor parameters for immutable configuration are fine, or to cache some data to members in OnReady.

## Mutating a set while iterating it

Using an immediate `Add` / `Remove` / `Clear` on the same set in the group you're currently iterating throws in DEBUG. In release the assertion is compiled out and iteration corrupts silently — entries get skipped, revisited, or (when an `Add` grows the buffer) read from freed memory.

**Fix.** Use the deferred set ops (the default) for changes-during-iteration, or stage them in a `NativeList<EntityHandle>` and apply after the loop. See [Sets — Immediate](../entity-management/sets.md#immediate).

## Forgetting to dispose pointers

Pointers must be manually disposed. DEBUG builds catch leaks at world shutdown and report them; release builds leak silently.

**Fix.** Dispose entity-owned pointers in an `OnRemoved` handler. See [Cleanup is manual](../advanced/heap-allocation-rules.md#cleanup-is-manual).

## Raw native collections in components don't round-trip through save/load

Component serialization copies the raw struct bytes (the blit fast-path). A `NativeList<T>` / `NativeHashMap<K,V>` value is essentially a pointer to externally-allocated storage plus a length. Round-tripping such a component through a snapshot copies the bytes verbatim — the pointer that comes back on load is the previous session's memory address, no longer mapped, freed, or reassigned. Reading the deserialized collection crashes or returns garbage.

Wrapping the collection in a `NativeUniquePtr<NativeList<T>>` avoids the trap: the unique ptr is a heap-key, not a raw memory pointer, and Trecs's serializer walks the inner collection's contents through the heap rather than blitting the struct.

**Fix.** Wrap any native collection that needs to survive serialization in a `NativeUniquePtr` (or `NativeSharedPtr`). See [Wrapping native collections](../advanced/heap.md#wrapping-native-collections).

## `NativeUniquePtr<NativeList<T>>` — inner storage must be disposed first

The wrapped collection's storage is allocated in Unity's allocator, not Trecs's heap. Disposing the `NativeUniquePtr` only frees the heap slot holding the `NativeList` header — the underlying allocation leaks.

**Fix.** Dispose the inner collection, then the unique ptr. See [Wrapping native collections](../advanced/heap.md#wrapping-native-collections).

## Looking up a fresh `EntityHandle` in the same fixed step

`World.AddEntity<T>()` returns immediately with an `EntityInitializer` whose `Handle` is valid as an identity (stable, will resolve to this exact entity later) — but the entity itself isn't placed in any group until submission, which runs at the end of the fixed step. Another system later in the same step that reads components on the handle throws, because the entity literally doesn't exist anywhere yet.

```csharp
// Fixed system A: spawn a child, store its handle on the parent
var childHandle = World.AddEntity<Child>().Set(...).Handle;
parent.ChildRef = childHandle;

// Fixed system B (same step, ordered later)
ref readonly var pos = ref World.Component<Position>(parent.ChildRef).Read;  // throws
```

**Fix.** Check `World.EntityExists(handle)` before dereferencing and skip if it returns false — the handle becomes dereferenceable on the next step, once submission has run.

## Native heap allocations aren't visible to jobs in the same step

Allocations into the native heaps (`NativeUniquePtr` / `NativeSharedPtr`) queue into a pending collection rather than the resolver's look up table that Burst jobs read from. The queue drains at submission time (end of fixed step). A Burst job scheduled in the same step that calls `.Get(NativeWorldAccessor)` on a freshly-allocated native ptr won't find it.

Main-thread `.Get(WorldAccessor)` / `.Set(WorldAccessor, ...)` calls **do** work on freshly-allocated native ptrs — the main-thread API path checks the pending queue first before the resolver. Managed pointers (`UniquePtr<T>` / `SharedPtr<T>`) aren't deferred either; they have no resolver layer and are main-thread-only by design.

The deferral exists because Burst jobs hold a snapshot of the resolver's allocation table; mutating it mid-job would corrupt in-flight reads.

**Fix.** Wait 1 frame, or if the work doesn't have to be Burst, do it from the main thread instead.

## Just-spawned entities haven't been fixed-updated when Presentation sees them

An entity spawned in a Fixed system is submitted in time for the same Tick's Presentation, but no fixed-update cycle has run on it yet — Presentation sees the spawn-time initial values, not the post-tick values. Therefore the entity will render at its initial state for one frame and then jump to the correct state on the next tick. Visible as a brief stutter or wrong-position pop on spawn.

**Fix.** Initialize *everything Presentation reads* at spawn time. If that's impractical, use an enabled flag, default this to false, then flip it during fixed update.

## Service-class accessor used during a Fixed system's `Execute`

During a `Fixed`-role system's `Execute`, only that system's own accessor may touch ECS state. Other accessors — even another `Fixed`-role one held by a service, even an `Unrestricted` one — throw if used mid-Fixed-execute. Recording access under the service's `DebugName` instead of the calling system's scrambles debug attribution and tooling, and `Unrestricted` accessors risk smuggling non-deterministic state into the simulation.

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

Variable-cadence phases (`EarlyPresentation` / `Presentation` / `LatePresentation`) and observer callbacks (`OnAdded` / `OnRemoved` / `OnMoved`) don't enforce this rule — services may freely use their own accessors there. Callbacks fire from inside `SubmitEntities`, which runs *between* Fixed-system executes rather than inside one.

**Fix.** Pass the calling system's `WorldAccessor` into the service rather than holding a separate one.

## Reading continuous time in fixed update with float-sensitive code

`DeltaTime` / `ElapsedTime` accumulate floating-point error that drifts across machines. For lockstep-deterministic workloads (RTS netcode etc.) this causes desync even when everything else is deterministic.

**Fix.** Set `WorldSettings.AssertNoTimeInFixedPhase = true` (throws on access during fixed phase) and use `World.FixedFrame` (a discrete tick counter) as your time source. See [`AssertNoTimeInFixedPhase`](../advanced/serialization.md#worldsettingsassertnotimeinfixedphase).  Only applicable for multiplayer games that require cross machine sync.

## Main-thread sync mid-phase stalls workers

Main-thread access through `WorldAccessor` (`.Read` / `.Write`) lazily completes conflicting in-flight jobs. Doing this in the middle of a phase while jobs are running stops the in-flight job, idles the workers, and tanks throughput.

**Fix.** Push main-thread reads/writes into a job, or order them after the job has had time to complete. See [Main-thread sync](../performance/dependency-tracking.md#main-thread-sync).
