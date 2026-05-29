# Trecs vs Unity ECS

For developers familiar with Unity's built-in ECS (the Entities package, 1.x line).

Trecs keeps a deliberately small API surface — a handful of high-level concepts, with source generation handling the low-level work. Two design choices drive most of the differences from Unity ECS: **how entities are identified** (tags, not just component sets) and **how they're stored in memory** (flat buffers, not chunks). Read those two sections first; the rest of the page is a feature-by-feature reference you can skim as needed.

## Tag-based identity

In Unity ECS an entity's identity is *emergent*: two entities with the same component set are indistinguishable in queries. Trecs adds a second axis — entities carry **tags** that queries filter on, independent of their components.

Tags come from **templates**: compile-time blueprints describing an entity's component layout and its identity tags. A tag may map 1:1 to one template (`GameTags.Spinner` on `SpinnerEntity`) or name an abstract role shared by many (`GameTags.Enemy`). You spawn by *tag*, not template — `AddEntity<GameTags.Enemy>()` — and the world resolves the registered template. Systems refer to entities by tag (or by component schema), never by template class, which decouples gameplay code from concrete entity definitions.

Both query styles are first-class:

```csharp
// By tag
[ForEachEntity(typeof(GameTags.Enemy), typeof(GameTags.Alive))]
void Execute(ref Health health, in Position pos) { ... }

// By component schema (Unity-ECS style)
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position pos, in Velocity vel) { ... }
```

Use tags when the subset is a design-level concept ("enemies"). Use `MatchByComponents` for cross-cutting systems (rendering, physics sync, debug overlays) that only care about component shape.

Why the extra axis is worth it:

- **Queries read like the design.** `[ForEachEntity(typeof(GameTags.Enemy))]` says what it means; "things with `Health` + `Position` + `AIState`" is a brittle stand-in for "enemies".
- **Loud failures on mismatches.** A tag-scoped query requesting a component its template doesn't declare is an error. Component-only matching silently skips the entity — a classic "why isn't my system running on this entity?" bug.
- **One source of truth.** Layout, defaults, tags, and partitions for "an Enemy" live in one template; spawn sites can't drift.
- **Safer refactoring.** Change template definition in one place instead of from each spawn site
- **Meaningful names in tools.** Logs, recordings, and inspectors name entities by what they are, not by an opaque component-set fingerprint.

The tradeoff: you define templates up front — no "attach a component and see what happens" path. For deliberately-designed simulations that's usually straightforward, and `MatchByComponents` covers the systems that don't care about identity.

## Memory layout: flat buffers vs chunks

Unity ECS splits each archetype across many fixed-size 16 KB chunks, and iteration walks them in order. Trecs uses structure-of-arrays: each group owns one flat, contiguous buffer per component, and an entity sits at the same index across all of them.

What that means in practice:

- **Iteration is a plain array walk.** `ForEachEntity` over a group is `for (int i = 0; i < count; i++)` against contiguous memory — no per-chunk bookkeeping or lookups.
- **Memory is sized to actual use.** A 16 KB chunk holding a handful of entities wastes space when rare archetypes proliferate; Trecs grows each group's buffers as entities are added.
- **Structural changes cost less — and can't happen by accident.** In Unity ECS, adding/removing a component moves the entity to another archetype, so reaching for "add a zero-sized tag as a cheap boolean" is secretly a full structural change (enableable components exist to dodge this). In Trecs the operations that copy memory between groups — [partition](../core/templates.md#partitions) transitions — are explicit, so they're hard to trigger accidentally. And for dynamic state that needn't move data at all, Trecs offers cheaper approaches such as [sets](../entity-management/sets.md), which track membership without touching component storage.
- **Different parallelism shape.** Both use Unity's job system. Unity ECS gives a chunk per worker; Trecs slices matching entities across `IJobParallelFor` batches. Throughput is comparable on large workloads.

The chunk model's advantage is per-chunk metadata: change-filter version numbers and chunk components let a parallel job skip an unchanged chunk wholesale. Flat buffers have no analog. On dense workloads throughput is otherwise comparable.

## Feature comparison

### Architecture

| | Unity ECS | Trecs |
|---|---|---|
| **Storage** | Archetype-based (entities grouped by component set) | Group-based (entities grouped by tag combination) |
| **Memory layout** | Fixed-size 16 KB chunks per archetype; entities split across many chunks | Structure-of-arrays: one contiguous buffer per component per group, indexed by the entity's position in the group |
| **Entity identity** | `Entity` (stable, wraps index + version) | `EntityHandle` (stable, wraps index + version) |
| **Definition** | No explicit templates (archetype emerges from components) | [Templates](../core/templates.md) (`ITemplate` + `ITagged`) |
| **Structural changes** | `EntityManager` calls are synchronous (a sync point). For deferral, opt into an `EntityCommandBuffer`; ECBs play back automatically at built-in command-buffer systems (e.g. `EndSimulationEntityCommandBufferSystem`), or manually via `Playback()`. | Structural changes are deferred — [submission](../entity-management/structural-changes.md) runs automatically at the end of every fixed step.  Alternatively can run synchronously via `World.Submit()` |
| **Multi-world** | Multiple `World` instances; default world auto-created. NetCode for Entities (separate package) adds explicit Client / Server / ThinClient world roles on top. | Multiple `World` instances; no built-in roles or cross-world bridging |

### Components

| | Unity ECS | Trecs |
|---|---|---|
| **Plain unmanaged data** | `IComponentData` (unmanaged struct) | `IEntityComponent` (unmanaged struct) |
| **Managed data on an entity** | `class IComponentData` (managed component) | No managed components — use a [heap pointer](../experimental/pointers.md) (`UniquePtr<T>` / `SharedPtr<T>`) referenced from a component |
| **Per-entity dynamic collections** | `IBufferElementData` / `DynamicBuffer<T>` (unbounded, separately allocated) | [`FixedList<N>`](../advanced/fixed-collections.md) (compile-time bounded, inline) for bounded cases; [`TrecsList<T>` / `TrecsArray<T>` / `TrecsDictionary<TKey, TValue>`](../experimental/dynamic-collections.md) (unbounded, snapshot-safe) for growable storage; or a [heap pointer](../experimental/pointers.md) (`UniquePtr<T>`) for managed data |
| **Tags / markers** | Zero-field `IComponentData` acts as a tag | Dedicated [`ITag`](../core/tags.md) marker structs (separate from components) |
| **Singletons / global state** | `SystemAPI.GetSingleton<T>()` | `World.GlobalComponent<T>()` on a [`Globals` template](../core/templates.md#global-entity-template) |
| **Single-field component shorthand** | None | [`[Unwrap]`](../core/components.md#the-unwrap-shorthand) — exposes the inner value through aspect properties (e.g. `boid.Position` is a `float3`, not a `Position` wrapper) |
| **Runtime add / remove of components** | Yes (`AddComponent` / `RemoveComponent`; causes archetype move) | No — a template's component set is fixed at compile time. Use [partitions](../core/templates.md#partitions), boolean / enum fields, [sets](../entity-management/sets.md), or child entities for the equivalents |
| **Components shared across many entities** | `ISharedComponentData` | No equivalent; share by reference via a [heap pointer](../experimental/pointers.md) |
| **Cleanup-after-destroy components** | `ICleanupComponentData` — persists past entity destruction so a system can finalize and explicitly remove them | None — use [`OnRemoved`](../entity-management/entity-events.md) observer events for finalization |
| **Per-chunk shared data** | Chunk components — one value attached per chunk (a 16 KB block of entities in the same archetype) | None — Trecs uses flat per-group buffers without sub-chunks; share via a [heap pointer](../experimental/pointers.md) or a `Globals` component |
| **Shared immutable blob assets** | `BlobAssetReference<T>` — structured immutable blobs shared across entities, baked into subscenes by a stable hash | [`SharedPtr<T>` / `NativeSharedPtr<T>`](../experimental/pointers.md) with [stable `BlobId`s](../experimental/shared-heap-data.md#pattern-b-look-up-by-stable-blobid) for cross-run identity |

### Systems

| | Unity ECS | Trecs |
|---|---|---|
| **System base type** | `ISystem` (struct, Burst-friendly) with `OnUpdate()`, or `SystemBase` (managed class, with `Entities.ForEach` lambda support) | [`ISystem`](../core/systems.md) (managed class) with `Execute()` |
| **One-time setup / teardown** | `OnCreate(ref SystemState)` / `OnDestroy(ref SystemState)` | [`partial void OnReady()`](../core/systems.md#onready-hook) / [`partial void OnShutdown()`](../core/systems.md#onshutdown-hook) — both source-generated `partial` hooks; `OnShutdown` runs in reverse of `OnReady` order |
| **Per-role access enforcement** | None — any system can call `EntityManager` for any operation | [Accessor roles](../advanced/accessor-roles.md) (`Fixed` / `Variable` / `Unrestricted`) — phase-derived permissions for component reads / writes, structural changes, RNG streams, and heap allocation |
| **System ordering** | `[UpdateAfter]` / `[UpdateBefore]` | [`[ExecuteAfter]` / `[ExecuteBefore]`](../core/systems.md#system-ordering) |
| **Phase / group structure** | System groups (`InitializationSystemGroup`, etc.) | [Five phases](../core/systems.md#phase-diagram) — EarlyPresentation, Input, Fixed, Presentation, LatePresentation |
| **Phase-aware time / frame counters** | Separate `Time.deltaTime` / `Time.fixedDeltaTime` properties; you pick the right one | `World.DeltaTime`, `World.Frame`, etc. resolve automatically to fixed or variable values based on the current phase |
| **Construction** | Framework discovers and instantiates systems via reflection | User instantiates systems explicitly and registers them |
| **System pause / disable** | Ad-hoc; no built-in mechanism | [`SetSystemPaused`](../advanced/pausing-and-disabling-systems.md) (deterministic, replay-stable) and `SetSystemEnabled(channel, ...)` (non-deterministic, multi-source) |
| **Burst on the system itself** | Struct `ISystem` can be Burst-compiled wholesale | Systems are managed classes; Burst is opt-in per job via [`[WrapAsJob]`](../performance/jobs-and-burst.md) |
| **Fixed → variable timestep smoothing** | Not built-in | Built-in [interpolation](../advanced/interpolation.md) framework |

### Queries

| | Unity ECS | Trecs |
|---|---|---|
| **Building a query** | `SystemAPI.Query<T>()`, `EntityQuery` via `GetEntityQuery()` | [`World.Query()`](../data-access/queries-and-iteration.md) builder (chain `WithTags<T>` / `WithComponents<T>` / `InSet<T>`), `MyAspect.Query(World)` for typed aspect iteration, or [`[ForEachEntity]`](../core/systems.md#foreachentity) method |
| **Bundled component access** | Aspects (`IAspect` + `RefRO`/`RefRW`) — *deprecated in Entities 1.x* | [Aspects](../data-access/aspects.md) (`IAspect` + `IRead`/`IWrite`) |
| **Iterating in a job** | `IJobEntity` filtered by `Execute` parameter types and `[WithAll]` / `[WithAny]` / `[WithNone]` attributes | `[ForEachEntity]` method on `IJobFor`, or [`[WrapAsJob]`](../performance/jobs-and-burst.md) |
| **Sparse / dynamic membership** | Enableable components (toggle without structural change) | [Sets](../entity-management/sets.md) — independent membership index, doesn't touch component storage |
| **Reactive lifecycle** | Change filters or `EntityCommandBuffer` patterns | First-class [`OnAdded` / `OnRemoved` / `OnMoved`](../entity-management/entity-events.md) subscriptions |
| **Detect component modifications** | Built-in change filters | None built-in |
| **Polymorphic aspect helpers** | None built-in | [Aspect interfaces](../advanced/aspect-interfaces.md) — `partial interface : IAspect` declares a contract that multiple aspects with matching component shapes can satisfy, enabling generic helpers without boxing or virtual dispatch |

### Jobs

| | Unity ECS | Trecs |
|---|---|---|
| **Entity-iterating job** | `IJobEntity` | [`[WrapAsJob]`](../performance/jobs-and-burst.md) / `IJobFor` + `[ForEachEntity]` |
| **Component lookup wiring** | Manual `GetComponentLookup` | [`[FromWorld]`](../advanced/advanced-jobs.md#fromworld-auto-wiring-job-fields) auto-wiring |
| **`JobHandle` dependency wiring** | Auto-tracked per-system via `state.Dependency` (framework threads input/output based on declared component access). Within a single system, multiple schedules are still hand-threaded through `state.Dependency`, and fan-in across handles uses `JobHandle.CombineDependencies`. Granularity is per component type globally. | Auto-wired per-job from declared component access — the [dependency tracker](../performance/dependency-tracking.md) infers the right input handle at every `ScheduleParallel`, including across multiple schedules in one system. Granularity is per `(component, group)`, so jobs touching the same component on different groups run in parallel. |
| **Structural ops from a job** | `EntityCommandBuffer.ParallelWriter` | [`NativeWorldAccessor`](../performance/jobs-and-burst.md#nativeworldaccessor) for structural ops |
| **Deterministic ordering of parallel ops** | `sortKey` on `EntityCommandBuffer` | [Sort keys](../entity-management/structural-changes.md#deterministic-submission) for deterministic ordering |

### Determinism & networking

| | Unity ECS | Trecs |
|---|---|---|
| **Stance toward determinism** | Nice-to-have, not enforced | Core design goal, enforced via API |
| **Recording / playback with desync detection** | Not built-in | Built-in via the [Trecs Player editor window](../editor-windows/player.md) — scrub back, replay forward, fork the timeline |
| **Deterministic RNG** | None built-in | Framework-level [`World.Rng`](../advanced/time-and-rng.md) with fork support |
| **Frame-isolated input for replay** | Not built-in (NetCode for Entities provides one) | [Input system](../core/input-system.md) with frame isolation |
| **Networking** | NetCode for Entities (separate package) — Client / Server / ThinClient world roles | No direct equivalent |

### Serialization

| | Unity ECS | Trecs |
|---|---|---|
| **Edit-time authoring → entities** | Subscene baking — designers author with `MonoBehaviour`s, baked at edit/build time | No direct equivalent (yet) |
| **Runtime save/load of full world state** | Limited runtime serialization | Built-in via the [Trecs Player editor window](../editor-windows/player.md) — capture, restore, and label full-state snapshots |
| **Incremental scene streaming** | Subscenes load incrementally (open-world / large-scale streaming) | None built-in — full snapshots only |
| **Custom type serializers** | None built-in for ECS save/load; managed/non-blittable types need bespoke save/load code | Register an [`ISerializer<T>`](../experimental/serialization.md) for any managed type stored on the heap; pure unmanaged components round-trip automatically |
| **Per-save versioning** | Handled outside the engine | `Reader.Version` / `Writer.Version` for evolving custom serializers across save-format revisions |

### Editor tooling

| | Unity ECS | Trecs |
|---|---|---|
| **Entity inspector** | Entity Debugger, Hierarchy window | [Hierarchy inspector](../editor-windows/hierarchy.md) |
| **Live editing of running entities** | Live baking — edits to subscene `MonoBehaviour` authoring sources are re-baked and flowed into the running entity world | [Hierarchy window](../editor-windows/hierarchy.md) — per-entity component inspector mutates running entities directly |
| **Record / scrub / fork timeline** | None built-in | [Player window](../editor-windows/player.md) — record a session, scrub back through earlier frames, fork the timeline |

### Physics & rendering

| | Unity ECS | Trecs |
|---|---|---|
| **Physics** | com.unity.physics | None |
| **Rendering** | Entities Graphics package | User-owned — DIY. Samples provide some common approaches |
| **Transform hierarchy** | Built-in `LocalTransform` + `Parent` / `Child` system maintaining `LocalToWorld` | None built-in — define your own components |
| **GameObject ↔ Entity hybrid** | Companion Components — an entity can carry a managed Unity `GameObject` "shadow" maintained by the framework | User-owned. Samples provide some common approaches |
