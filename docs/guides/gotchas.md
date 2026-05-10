# Gotchas

Common mistakes, edge cases, and surprises when building with Trecs. Each entry documents what goes wrong, why, and the fix. For the high-level rules, see [Best Practices](best-practices.md); for tooling-driven debugging, see [Debugging & Troubleshooting](debugging.md).

## Determinism

### `UnityEngine.Random` or `Time.deltaTime` in fixed update

Both vary across runs and break replay. The simulation desyncs from a recording the moment one of these executes during a fixed step.

**Fix.** Use `World.Rng` (with the `FixedRng` / `VariableRng` streams) and `world.FixedDeltaTime` / `world.DeltaTime` (phase-aware). See [Time & RNG](../advanced/time-and-rng.md).

### Mutable state stored on a fixed-update system

System fields are not serialized. Anything mutable kept on a system silently diverges between record and replay — the recording captures component state up to disk, and on replay the system field starts at its constructor default while the recording assumes the accumulated value.

**Fix.** Store dynamic state in components. Constructor parameters for immutable configuration are fine.

### Reading continuous time in fixed update with float-sensitive code

`DeltaTime` / `ElapsedTime` accumulate floating-point error that drifts across machines. For lockstep-deterministic workloads (RTS netcode etc.) this causes desync even when everything else is deterministic.

**Fix.** Set `WorldSettings.AssertNoTimeInFixedPhase = true` (throws on access during fixed phase) and use `World.FixedFrame` (a discrete tick counter) as your time source. See [`AssertNoTimeInFixedPhase`](../advanced/serialization.md#worldsettingsassertnotimeinfixedphase).

### Missing sort keys in parallel structural-change jobs

`NativeWorldAccessor` ops (`AddEntity` / `RemoveEntity` / `MoveTo` from a job) without a deterministic sort key produce non-deterministic ordering across runs.

**Fix.** Pass a stable sort key to every parallel structural op. See [Deterministic submission](../entity-management/structural-changes.md#deterministic-submission).

## Lifecycle & init

### Reading globals from `OnReady`

The global entity isn't yet initialized when `OnReady` runs. `World.GlobalComponent<T>()` from there is unsafe.

**Fix.** Use `OnReady` for one-time setup that doesn't touch globals — typically subscribing to events. Initialize global state from a one-shot fixed-phase system that runs at frame 0, or from world setup before any system's first execute. See [OnReady hook](../core/systems.md#onready-hook).

### Service-class accessor used during a Fixed system's `Execute`

Strict-accessor rule: during a `Fixed` system's `Execute`, only that system's own accessor may touch ECS state. A service holding a separately-created accessor — even a `Fixed`-role one — throws.

**Fix.** Pass the calling system's `WorldAccessor` into the service rather than holding a separate one. Variable-cadence phases and observer callbacks don't enforce this rule. See [Strict-accessor rule](../advanced/accessor-roles.md#strict-accessor-during-fixed-execute-rule).

### Cascading structural changes from event callbacks

`OnAdded` / `OnRemoved` / `OnMoved` callbacks fire from inside `SubmitEntities`. Structural changes from inside a callback queue further callbacks, which run before submission completes — so a poorly-bounded cascade loops.

**Fix.** Cap recursion depth or use deferred patterns. See [Cascading structural changes from callbacks](../entity-management/entity-events.md#cascading-structural-changes-from-callbacks).

## Structural changes

### Same-step Fixed visibility

`AddEntity` / `RemoveEntity` / `MoveTo` are queued and applied at submission, which runs at the end of each fixed step. A Fixed system later in the same step does *not* see the new state — it sees it on the next tick. Presentation systems within the same `Tick()` *do* see it, since submission runs between Fixed and Presentation.

**Fix.** Call `World.SubmitEntities()` manually if same-step visibility is required, or accept the one-tick delay (typically fine).

### Just-spawned entities haven't been fixed-updated when Presentation sees them

Submission runs at end of fixed step (and end of `Tick()`), so an entity spawned in a Fixed system *does* exist in time for Presentation in the same Tick. But it's been through **zero** fixed-update cycles — Presentation sees its spawn-time initial values, not whatever a fixed tick of physics / AI / logic would have produced.

If presentation logic implicitly assumes "at least one fixed tick has run on this entity" — interpolation between fixed-step snapshots, derived per-tick state, transform-sync that depends on a value integrated in Fixed, a GameObject created in `OnAdded` whose pose is driven from `[VariableUpdateOnly]` fields populated by a fixed system — the entity will render at its initial state for one frame and then jump to the correct state on the next tick. Visible as a brief stutter or wrong-position pop on spawn.

**Fix.** Initialize *everything Presentation reads* at spawn time, including interpolation snapshots and any derived state that fixed-update would normally produce. If that's impractical, mark new entities with a "Spawning" tag/set/flag that Presentation skips for one tick, and clear the marker on the entity's first Fixed pass.

### Runtime add / remove of components doesn't exist

A template's component set is fixed at compile time. There is no `AddComponent<T>(entity)` / `RemoveComponent<T>(entity)`.

**Fix.** Use one of the escape hatches: [partitions](../core/templates.md#partitions), boolean / enum fields on a component, [sets](../entity-management/sets.md), or a child entity referenced by `EntityHandle`.

## Heap & disposal

### `NativeUniquePtr<NativeList<T>>` — inner storage must be disposed first

The wrapped collection's storage is allocated in Unity's allocator, not Trecs's heap. Disposing the `NativeUniquePtr` only frees the heap slot holding the `NativeList` header — the underlying allocation leaks.

**Fix.** Dispose the inner collection, then the unique ptr. See [Wrapping native collections](../advanced/heap.md#wrapping-native-collections).

### Forgetting to dispose pointers

Pointers must be manually disposed. DEBUG builds catch leaks at world shutdown and report them; release builds leak silently.

**Fix.** Dispose entity-owned pointers in an `OnRemoved` handler. See [Cleanup is manual](../advanced/heap-allocation-rules.md#cleanup-is-manual).

### Frame-scoped heap allocation outside `Input`

Frame-scoped heaps (`AllocFrameScopedUnique`, native variants) are `Input`-only. Calling them from a Fixed or Variable system throws.

**Fix.** Allocate frame-scoped data from an `Input`-phase system, or use a persistent heap (`AllocUnique` / `AllocShared`) and dispose explicitly.

### Allocating persistent heap from `Variable` / `Presentation`

Persistent heap (`SharedPtr` / `NativeSharedPtr`, etc.) is `Fixed`-only. The role-check throws if a presentation-phase system tries to allocate.

**Fix.** Move the allocation to a Fixed system, or use a frame-scoped heap from an Input system if the lifetime is one frame.

## Sets & iteration

### Mutating a set while iterating it

Using an immediate `Add` / `Remove` / `Clear` on the same set in the group you're currently iterating throws in DEBUG. In release the assertion is compiled out and iteration corrupts silently — entries get skipped, revisited, or (when an `Add` grows the buffer) read from freed memory.

**Fix.** Use the deferred set ops (the default) for changes-during-iteration, or stage them in a `NativeList<EntityHandle>` and apply after the loop. See [Sets — Immediate](../entity-management/sets.md#immediate).

## Input

### `[Input]` components are read-only outside Input systems

Direct writes to `[Input]`-marked component fields from a Fixed (or any non-Input) system throw in DEBUG. Values must enter through `World.AddInput<T>(...)` so recording and playback can replay them.

**Fix.** If you need a sim-state field that fixed-update systems can mutate, use a regular (non-`[Input]`) component.

### Polling one-shot key-down inside `Execute`

Input-phase `Execute()` runs once per fixed step (not once per rendered frame). One-shot Unity events like `Input.GetKeyDown` only fire on the variable frame the key was pressed; if a fixed step doesn't run on that frame, an `Execute` poll misses the event entirely.

**Fix.** Capture the event at variable cadence (a `MonoBehaviour Update` or an early-presentation system) into a buffered field, and forward the latest value in `Execute()`. See [Input System — Queuing input](../core/input-system.md#queuing-input).

## Threading & jobs

### Main-thread sync mid-phase stalls workers

Main-thread access through `WorldAccessor` (`.Read` / `.Write`) lazily completes conflicting in-flight jobs. Doing this in the middle of a phase while jobs are running stops the in-flight job, idles the workers, and tanks throughput.

**Fix.** Push main-thread reads/writes into a job, or order them after the job has had time to complete. See [Main-thread sync](../performance/dependency-tracking.md#main-thread-sync).

## Serialization

### Field names in custom serializers are not persisted

The `name` arguments to `writer.Write("name", value)` / `reader.Read<T>("name")` are debug labels only. They never reach the binary stream — the format is purely positional.

**Implications.**

- Renaming a field is a no-op on disk.
- Reordering reads silently corrupts deserialization.
- Adding or removing one without bumping the format version produces silently wrong data.

**Fix.** Bump `version` on every layout change and branch on `reader.Version` to decode old layouts. See [Schema versioning](../advanced/serialization.md#schema-versioning).
