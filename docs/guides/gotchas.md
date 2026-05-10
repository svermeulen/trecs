# Gotchas

Common mistakes, edge cases, and surprises when building with Trecs. Each entry documents what goes wrong, why, and the fix. For the high-level rules, see [Best Practices](best-practices.md); for tooling-driven debugging, see [Debugging & Troubleshooting](debugging.md).

### `UnityEngine.Random` or `Time.deltaTime` in fixed update

Both vary across runs and break replay. The simulation desyncs from a recording the moment one of these are used during a fixed step.

**Fix.** Use `World.Rng` and `World.DeltaTime` (phase-aware). See [Time & RNG](../advanced/time-and-rng.md).

### Mutable state stored on a fixed-update system

System fields are not serialized. Anything mutable kept on a system silently diverges between record and replay.

**Fix.** Store dynamic state in components. Constructor parameters for immutable configuration are fine, or to cache some data to members in OnReady.

### Reading continuous time in fixed update with float-sensitive code

`DeltaTime` / `ElapsedTime` accumulate floating-point error that drifts across machines. For lockstep-deterministic workloads (RTS netcode etc.) this causes desync even when everything else is deterministic.

**Fix.** Set `WorldSettings.AssertNoTimeInFixedPhase = true` (throws on access during fixed phase) and use `World.FixedFrame` (a discrete tick counter) as your time source. See [`AssertNoTimeInFixedPhase`](../advanced/serialization.md#worldsettingsassertnotimeinfixedphase).  Only applicable for multiplayer games that require cross machine sync.

### Service-class accessor used during a Fixed system's `Execute`

Strict-accessor rule: during a `Fixed` system's `Execute`, only that system's own accessor may touch ECS state. A service holding a separately-created accessor — even a `Fixed`-role one — throws.

**Fix.** Pass the calling system's `WorldAccessor` into the service rather than holding a separate one. Variable-cadence phases and observer callbacks don't enforce this rule. See [Strict-accessor rule](../advanced/accessor-roles.md#strict-accessor-during-fixed-execute-rule).

### Just-spawned entities haven't been fixed-updated when Presentation sees them

An entity spawned in a Fixed system is submitted in time for the same Tick's Presentation, but no fixed-update cycle has run on it yet — Presentation sees the spawn-time initial values, not the post-tick values. Therefore the entity will render at its initial state for one frame and then jump to the correct state on the next tick. Visible as a brief stutter or wrong-position pop on spawn.

**Fix.** Initialize *everything Presentation reads* at spawn time. If that's impractical, use an enabled flag, default this to false, then flip it during fixed update.

### Raw native collections in components don't round-trip through save/load

Component serialization copies the raw struct bytes (the blit fast-path). A `NativeList<T>` / `NativeHashMap<K,V>` value is essentially a pointer to externally-allocated storage plus a length. Round-tripping such a component through a snapshot copies the bytes verbatim — the pointer that comes back on load is the previous session's memory address, no longer mapped, freed, or reassigned. Reading the deserialized collection crashes or returns garbage.

Wrapping the collection in a `NativeUniquePtr<NativeList<T>>` avoids the trap: the unique ptr is a heap-key, not a raw memory pointer, and Trecs's serializer walks the inner collection's contents through the heap rather than blitting the struct.

**Fix.** Wrap any native collection that needs to survive serialization in a `NativeUniquePtr` (or `NativeSharedPtr`). See [Wrapping native collections](../advanced/heap.md#wrapping-native-collections).

### `NativeUniquePtr<NativeList<T>>` — inner storage must be disposed first

The wrapped collection's storage is allocated in Unity's allocator, not Trecs's heap. Disposing the `NativeUniquePtr` only frees the heap slot holding the `NativeList` header — the underlying allocation leaks.

**Fix.** Dispose the inner collection, then the unique ptr. See [Wrapping native collections](../advanced/heap.md#wrapping-native-collections).

### Forgetting to dispose pointers

Pointers must be manually disposed. DEBUG builds catch leaks at world shutdown and report them; release builds leak silently.

**Fix.** Dispose entity-owned pointers in an `OnRemoved` handler. See [Cleanup is manual](../advanced/heap-allocation-rules.md#cleanup-is-manual).

### Mutating a set while iterating it

Using an immediate `Add` / `Remove` / `Clear` on the same set in the group you're currently iterating throws in DEBUG. In release the assertion is compiled out and iteration corrupts silently — entries get skipped, revisited, or (when an `Add` grows the buffer) read from freed memory.

**Fix.** Use the deferred set ops (the default) for changes-during-iteration, or stage them in a `NativeList<EntityHandle>` and apply after the loop. See [Sets — Immediate](../entity-management/sets.md#immediate).

### Main-thread sync mid-phase stalls workers

Main-thread access through `WorldAccessor` (`.Read` / `.Write`) lazily completes conflicting in-flight jobs. Doing this in the middle of a phase while jobs are running stops the in-flight job, idles the workers, and tanks throughput.

**Fix.** Push main-thread reads/writes into a job, or order them after the job has had time to complete. See [Main-thread sync](../performance/dependency-tracking.md#main-thread-sync).

