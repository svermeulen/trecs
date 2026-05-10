# Trecs vs Unity ECS

A comparison for developers familiar with Unity's built-in ECS (Entities package).

Trecs has a deliberately small API surface — a handful of core high level concepts, with source generation doing the heavy lifting for low level operations.

## Architecture

| | Unity ECS | Trecs |
|---|---|---|
| **Storage** | Archetype-based (entities grouped by component set) | Group-based (entities grouped by tag combination) |
| **Memory layout** | Fixed-size 16 KB chunks per archetype; entities split across many chunks | Structure-of-arrays: one contiguous buffer per component per group, indexed by the entity's position in the group |
| **Entity identity** | `Entity` (stable, wraps index + version) | `EntityHandle` (stable, wraps index + version) |
| **Definition** | No explicit templates (archetype emerges from components) | [Templates](../core/templates.md) (`ITemplate` + `ITagged`) |
| **Structural changes** | `EntityManager` calls are synchronous (a sync point). For deferral, opt into an `EntityCommandBuffer`; ECBs play back automatically at built-in command-buffer systems (e.g. `EndSimulationEntityCommandBufferSystem`), or manually via `Playback()`. | Structural changes are deferred — [submission](../entity-management/structural-changes.md) runs automatically at the end of every fixed step.  Alternatively can run synchronously via `World.SubmitEntities()` |
| **Multi-world** | Multiple `World` instances; default world auto-created. NetCode for Entities (separate package) adds explicit Client / Server / ThinClient world roles on top. | Multiple `World` instances; no built-in roles or cross-world bridging |

## Components

| | Unity ECS | Trecs |
|---|---|---|
| **Plain unmanaged data** | `IComponentData` (unmanaged struct) | `IEntityComponent` (unmanaged struct) |
| **Managed data on an entity** | `class IComponentData` (managed component) | No managed components — use a [heap pointer](../advanced/heap.md) (`UniquePtr<T>` / `SharedPtr<T>`) referenced from a component |
| **Per-entity dynamic collections** | `IBufferElementData` / `DynamicBuffer<T>` (unbounded, separately allocated) | [`FixedList<N>`](../advanced/fixed-collections.md) (compile-time bounded, inline) for bounded cases, or a [heap pointer](../advanced/heap.md) (`UniquePtr<List<T>>` / `NativeUniquePtr<NativeList<T>>`) for unbounded |
| **Tags / markers** | Zero-field `IComponentData` acts as a tag | Dedicated [`ITag`](../core/tags.md) marker structs (separate from components) |
| **Singletons / global state** | `SystemAPI.GetSingleton<T>()` | `World.GlobalComponent<T>()` on a [`Globals` template](../core/templates.md#global-entity-template) |
| **Single-field component shorthand** | None | [`[Unwrap]`](../core/components.md#the-unwrap-shorthand) — exposes the inner value through aspect properties (e.g. `boid.Position` is a `float3`, not a `Position` wrapper) |
| **Runtime add / remove of components** | Yes (`AddComponent` / `RemoveComponent`; causes archetype move) | No — a template's component set is fixed at compile time. Use [partitions](../core/templates.md#partitions), boolean / enum fields, [sets](../entity-management/sets.md), or child entities for the equivalents |
| **Components shared across many entities** | `ISharedComponentData` | No equivalent; share by reference via a heap pointer |
| **Cleanup-after-destroy components** | `ICleanupComponentData` — persists past entity destruction so a system can finalize and explicitly remove them | None — use [`OnRemoved`](../entity-management/entity-events.md) observer events for finalization |
| **Per-chunk shared data** | Chunk components — one value attached per chunk (a 16 KB block of entities in the same archetype) | None — Trecs uses flat per-group buffers without sub-chunks; share via a [heap pointer](../advanced/heap.md) or a `Globals` component |
| **Shared immutable blob assets** | `BlobAssetReference<T>` — structured immutable blobs shared across entities, baked into subscenes by a stable hash | [`SharedPtr<T>` / `NativeSharedPtr<T>`](../advanced/heap.md) with [stable `BlobId`s](../advanced/heap-allocation-rules.md#stable-blobids-when-init-isnt-deterministic) for cross-run identity |

## Systems

| | Unity ECS | Trecs |
|---|---|---|
| **System base type** | `ISystem` (struct, Burst-friendly) with `OnUpdate()`, or `SystemBase` (managed class, with `Entities.ForEach` lambda support) | [`ISystem`](../core/systems.md) (managed class) with `Execute()` |
| **One-time setup / teardown** | `OnCreate(ref SystemState)` / `OnDestroy(ref SystemState)` | [`partial void OnReady()`](../core/systems.md#onready-hook); implement `IDisposable` on the system for teardown |
| **Per-role access enforcement** | None — any system can call `EntityManager` for any operation | [Accessor roles](../advanced/accessor-roles.md) (`Fixed` / `Variable` / `Unrestricted`) — phase-derived permissions for component reads / writes, structural changes, RNG streams, and heap allocation |
| **System ordering** | `[UpdateAfter]` / `[UpdateBefore]` | [`[ExecuteAfter]` / `[ExecuteBefore]`](../core/systems.md#system-ordering) |
| **Phase / group structure** | System groups (`InitializationSystemGroup`, etc.) | [Five fixed phases](../core/systems.md#update-phases) — EarlyPresentation, Input, Fixed, Presentation, LatePresentation |
| **Phase-aware time / frame counters** | Separate `Time.deltaTime` / `Time.fixedDeltaTime` properties; you pick the right one | `World.DeltaTime`, `World.Frame`, etc. resolve automatically to fixed or variable values based on the current phase |
| **Construction** | Framework discovers and instantiates systems via reflection | User instantiates systems explicitly and registers them |
| **System pause / disable** | Ad-hoc; no built-in mechanism | [`SetSystemPaused`](../advanced/system-control.md) (deterministic, replay-stable) and `SetSystemEnabled(channel, ...)` (non-deterministic, multi-source) |
| **Burst on the system itself** | Struct `ISystem` can be Burst-compiled wholesale | Systems are managed classes; Burst is opt-in per job via [`[WrapAsJob]`](../performance/jobs-and-burst.md) |
| **Fixed → variable timestep smoothing** | Not built-in | Built-in [interpolation](../advanced/interpolation.md) framework |

## Queries

| | Unity ECS | Trecs |
|---|---|---|
| **Building a query** | `SystemAPI.Query<T>()`, `EntityQuery` via `GetEntityQuery()` | [`World.Query()`](../data-access/queries-and-iteration.md) builder (chain `WithTags<T>` / `WithComponents<T>` / `InSet<T>`), `MyAspect.Query(World)` for typed aspect iteration, or [`[ForEachEntity]`](../core/systems.md#foreachentity) method |
| **Bundled component access** | Aspects (`IAspect` + `RefRO`/`RefRW`) | [Aspects](../data-access/aspects.md) (`IAspect` + `IRead`/`IWrite`) |
| **Iterating in a job** | `IJobEntity` filtered by `Execute` parameter types and `[WithAll]` / `[WithAny]` / `[WithNone]` attributes | `[ForEachEntity]` method on `IJobFor`, or [`[WrapAsJob]`](../performance/jobs-and-burst.md) |
| **Sparse / dynamic membership** | Enableable components (toggle without structural change) | [Sets](../entity-management/sets.md) — independent membership index, doesn't touch component storage |
| **Reactive lifecycle** | Change filters or `EntityCommandBuffer` patterns | First-class [`OnAdded` / `OnRemoved` / `OnMoved`](../entity-management/entity-events.md) subscriptions |
| **Detect component modifications** | Built-in change filters | None built-in |
| **Polymorphic aspect helpers** | Generic methods over `IAspect` work but no declared contract | [Aspect interfaces](../advanced/aspect-interfaces.md) — `partial interface : IAspect` declares a contract that multiple aspects with matching component shapes can satisfy, enabling generic helpers without boxing or virtual dispatch |

## Jobs

| | Unity ECS | Trecs |
|---|---|---|
| **Entity-iterating job** | `IJobEntity` | [`[WrapAsJob]`](../performance/jobs-and-burst.md) + `[ForEachEntity]` |
| **Component lookup wiring** | Manual `GetComponentLookup` | [`[FromWorld]`](../advanced/advanced-jobs.md#fromworld--auto-wiring-job-fields) auto-wiring |
| **`JobHandle` dependency wiring** | Manual `JobHandle.CombineDependencies`; you thread input handles between schedules | Auto-wired from declared component access — the [dependency tracker](../performance/dependency-tracking.md) infers the right input handle at each `ScheduleParallel` |
| **Structural ops from a job** | `EntityCommandBuffer.ParallelWriter` | [`NativeWorldAccessor`](../performance/jobs-and-burst.md#nativeworldaccessor) for structural ops |
| **Deterministic ordering of parallel ops** | `sortKey` on `EntityCommandBuffer` | [Sort keys](../entity-management/structural-changes.md#deterministic-submission) for deterministic ordering |

## Determinism & networking

| | Unity ECS | Trecs |
|---|---|---|
| **Stance toward determinism** | Nice-to-have, not enforced | Core design goal, enforced via API |
| **Recording / playback with desync detection** | Not built-in | Built-in [recording / playback](../advanced/recording-and-playback.md) |
| **Deterministic RNG** | None built-in | Framework-level [`World.Rng`](../advanced/time-and-rng.md) with fork support |
| **Frame-isolated input for replay** | Not built-in (NetCode for Entities provides one) | [Input system](../core/input-system.md) with frame isolation |
| **Networking** | NetCode for Entities (separate package) — Client / Server / ThinClient world roles | No direct equivalent |

## Serialization

| | Unity ECS | Trecs |
|---|---|---|
| **Edit-time authoring → entities** | Subscene baking — designers author with `MonoBehaviour`s, baked at edit/build time | No direct equivalent (yet) |
| **Runtime save/load of full world state** | Limited runtime serialization | Built-in [serialization](../advanced/serialization.md) (snapshots, save/load, replays) |
| **Incremental scene streaming** | Subscenes load incrementally (open-world / large-scale streaming) | None built-in — full snapshots only |
| **Delta-encoded snapshots** | None built-in | Delta encoding for compact snapshots |
| **Per-save versioning** | Handled outside the engine | `Reader.Version` / `Writer.Version` for evolving custom serializers across save format revisions |

## Editor tooling

| | Unity ECS | Trecs |
|---|---|---|
| **Entity inspector** | Entity Debugger, Hierarchy window | [Hierarchy inspector](../editor-windows/hierarchy.md) |
| **Live editing of running entities** | Live baking — edits to subscene `MonoBehaviour` authoring sources are re-baked and flowed into the running entity world | [Hierarchy window](../editor-windows/hierarchy.md) — per-entity JSON-editable component inspector mutates running entities directly |
| **Record / scrub / fork timeline** | None built-in | [Player window](../editor-windows/player.md) — record a session, scrub back through earlier frames, fork the timeline |

## Physics & rendering

| | Unity ECS | Trecs |
|---|---|---|
| **Physics** | Unity Physics (built-in) | External (via OOP bridge) |
| **Rendering** | Entities Graphics package | User-owned — bring your own approach |
| **Transform hierarchy** | Built-in `LocalTransform` + `Parent` / `Child` system maintaining `LocalToWorld` | None built-in — define your own components |
| **GameObject ↔ Entity hybrid** | Companion Components — an entity can carry a managed Unity `GameObject` "shadow" maintained by the framework | Manual bridging via a `GameObjectId` component plus a user-side registry (the samples ship a `GameObjectRegistry` helper) |

## Tag-based identity

The most consequential architectural difference is that Trecs gives entities an identity axis beyond "which components do they carry". In Unity ECS, identity is *emergent* — two entities with the same component set are indistinguishable in queries. In Trecs, entities also carry **tags** that queries can filter on.

A tag may map 1:1 to one template (`GameTags.Spinner` only on `SpinnerEntity`) or name an abstract role shared across many templates that inherit from a common base. **Templates** themselves are compile-time blueprints describing an entity's component layout and identity tags. Spawn sites name the *tag*, not the template (`AddEntity<GameTags.Enemy>()`), and the world looks up the template registered for it. Systems should also refer to entities by tag (or by component schema), never by template class — that's what keeps gameplay code decoupled from concrete entity definitions.

Both styles are first-class:

```csharp
// By tag
[ForEachEntity(typeof(GameTags.Enemy), typeof(GameTags.Alive))]
void Execute(ref Health health, in Position pos) { ... }

// By component schema (Unity-ECS style)
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position pos, in Velocity vel) { ... }
```

Tag scoping is useful when the subset is a design-level concept rather than a component-schema coincidence. `MatchByComponents` is right for cross-cutting systems (rendering, physics sync, debug overlays) that don't care *what* an entity is, only that it has certain components.

The extra identity axis buys you a few practical things:

- **Queries can read like the design.** `[ForEachEntity(typeof(GameTags.Enemy))]` says what it means. Component-set queries drift: "things with `Health` + `Position` + `AIState`" is a brittle stand-in for "enemies".
- **Tag queries fail loudly when shapes are wrong.** A tag-scoped query whose matching template doesn't declare a requested component surfaces an error. Component-only matching silently skips the entity instead — a notoriously common source of "why isn't my system running on this entity?" bugs.
- **Templates are a single source of truth.** The full layout, defaults, tags, and partitions for "an Enemy" live in one place. Spawn sites can't drift.
- **Refactoring is safer.** Renaming a component is one template edit; the generator surfaces every spawn site that's affected.
- **Tags carry meaning into tools.** Logs, recordings, and inspectors can name entities by what they are rather than by an opaque component-set fingerprint.

The tradeoff: you have to define templates up front for the entities you want to spawn — there's no "just attach a component and see what happens" path. For deliberately-designed simulations that's usually a feature, and `MatchByComponents` is still there for systems that genuinely don't care about identity.

## Memory layout: flat buffers vs chunks

Unity ECS splits each archetype across many fixed-size 16 KB chunks; iteration walks chunks in order. Trecs uses structure-of-arrays: each group owns one flat contiguous buffer per component, and every entity occupies the same index across all of them.

The practical consequences:

- **Iteration is a plain array walk.** `ForEachEntity` over a group is `for (int i = 0; i < count; i++)` against contiguous memory — no per-chunk bookkeeping, no chunk lookups.
- **Memory is sized to actual use.** A 16 KB chunk is allocated even when it only holds a handful of entities, so rare archetypes can waste meaningful memory when they proliferate. Trecs grows each group's buffers as entities are added.
- **Structural changes cost less, and there's less opportunity to do them accidentally.** In Unity ECS, adding/removing a component moves the entity to a different archetype; users coming from a tag-heavy mindset sometimes reach for "add a zero-sized tag component" as a cheap boolean, not realizing it's a full structural change. Unity's enableable components exist to fix that. In Trecs the foot-gun doesn't exist: a template's component set is fixed at compile time, so you can't add or remove components at runtime at all. The equivalents — partition transitions, boolean component fields, sets — are visibly what they are at the call site.
- **Parallelism shape is different.** Both engines use Unity's job system. Unity ECS naturally parallelizes a chunk per worker; Trecs slices a query's matching entities across `IJobParallelFor` batches. Throughput is comparable for large workloads.

Neither layout is universally better — chunks shine when you have many small archetypes with aggressive per-chunk filtering; flat per-group buffers shine when the entity-kind set is stable and queries are tag-scoped.
