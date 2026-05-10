# Trecs vs Unity ECS

A comparison for developers familiar with Unity's built-in ECS (Entities package).

Trecs has a deliberately small API surface — a handful of core concepts, with source generation doing the heavy lifting. Unity ECS covers more ground (multiple system base types, several iteration patterns, several component flavors, the baking pipeline, NetCode) and exposes a much larger API surface to match. For the kind of project Trecs targets — games where determinism, replays, and networked rollback are first-class concerns — the smaller surface is part of the point: less to learn, less to get wrong.

## Architecture

| | Trecs | Unity ECS |
|---|---|---|
| **Storage** | Group-based (entities grouped by tag combination) | Archetype-based (entities grouped by component set) |
| **Memory layout** | Structure-of-arrays: one contiguous buffer per component per group, indexed by the entity's position in the group | Fixed-size 16 KB chunks per archetype; entities split across many chunks |
| **Entity identity** | `EntityHandle` (stable) + `EntityIndex` (transient) | `Entity` (stable, wraps index + version) |
| **Definition** | Templates (`ITemplate` + `ITagged`) | No explicit templates (archetype emerges from components) |
| **Structural changes** | Deferred, applied at submission | Deferred via `EntityCommandBuffer` |
| **Multi-world** | Multiple `World` instances; no built-in roles or cross-world bridging | Explicit Client / Server / ThinClient worlds wired into NetCode for Entities |

## Key differences

### Components

| Trecs | Unity ECS |
|---|---|
| `IEntityComponent` (unmanaged struct) | `IComponentData` (unmanaged struct) |
| No managed components | `class IComponentData` (managed) |
| [`FixedList<N>`](../advanced/fixed-collections.md) (compile-time bounded, inline) | `IBufferElementData` / `DynamicBuffer<T>` (unbounded, separately allocated) |
| Tags via `ITag` (separate from components) | Zero-field `IComponentData` acts as tag |
| `[Unwrap]` for single-field components | No equivalent |
| Global state via `World.GlobalComponent<T>()` on a `Globals` template | Singletons via `SystemAPI.GetSingleton<T>()` |

### Systems

| Trecs | Unity ECS |
|---|---|
| `ISystem` with `Execute()` | `ISystem` with `OnUpdate()` |
| `[ForEachEntity]` source generation | `SystemAPI.Query<T>()` |
| `[ExecuteAfter]` / `[ExecuteBefore]` | `[UpdateAfter]` / `[UpdateBefore]` |
| Five phases (EarlyPresentation, Input, Fixed, Presentation, LatePresentation) | System groups (`InitializationSystemGroup`, etc.) |
| User instantiates systems explicitly (`new FooSystem(dep1, dep2)`) and registers them | Framework discovers and instantiates systems via reflection |
| Systems are managed classes; Burst is opt-in per job via `[WrapAsJob]` | Struct `ISystem` can be Burst-compiled wholesale |

### Queries

| Trecs | Unity ECS |
|---|---|
| `World.Query().WithTags<T>()` | `EntityQuery` via `GetEntityQuery()` |
| Aspects (`IAspect` + `IRead`/`IWrite`) | Aspects (`IAspect` + `RefRO`/`RefRW`) |
| `[ForEachEntity]` with tag/component scope | `IJobEntity` with query attributes |
| [Sets](../entity-management/sets.md) for sparse filtering | Enableable components |
| Reactive lifecycle via `World.Events.EntitiesWithTags<T>().OnAdded` / `OnRemoved` | No first-class equivalent |

### Jobs

| Trecs | Unity ECS |
|---|---|
| `[WrapAsJob]` + `[ForEachEntity]` | `IJobEntity` |
| `[FromWorld]` auto-wiring | Manual `GetComponentLookup` |
| `NativeWorldAccessor` for structural ops | `EntityCommandBuffer.ParallelWriter` |
| Sort keys for deterministic ordering | `sortKey` on `EntityCommandBuffer` |

### Determinism & networking

| Trecs | Unity ECS |
|---|---|
| Core design goal | Nice-to-have |
| Built-in recording / playback with desync detection | No built-in equivalent |
| Deterministic RNG (`World.Rng`) | No built-in equivalent |
| [Input system](../core/input-system.md) with frame isolation for perfect replay | NetCode for Entities (separate package) |

### Serialization

| Trecs | Unity ECS |
|---|---|
| Full world state serialization (runtime save/load, snapshots, replays) | Subscene baking (edit-time); limited runtime serialization |
| Stable type IDs (auto-generated from type name) | `TypeIndex` (auto-generated) |
| Delta serialization support | No built-in equivalent |
| Per-save versioning via `Reader.Version` / `Writer.Version` for evolving custom serializers | No canonical equivalent — handled outside the engine |

### Editor tooling

| Trecs | Unity ECS |
|---|---|
| Hierarchy inspector + transport / scrub / record window | Entity Debugger, Hierarchy window |

### Authoring

| Trecs | Unity ECS |
|---|---|
| Programmatic — a composition root constructs the world, registers templates and systems, and spawns entities in code | Subscene + `Baker` — designers author with `MonoBehaviour`s, baked into entities at edit/build time |

### Physics & rendering

| Trecs | Unity ECS |
|---|---|
| External (via OOP bridge) | Unity Physics (built-in) |
| Rendering is user-owned — bring your own approach | Entities Graphics package |
| No built-in transform hierarchy — define your own components | Built-in `LocalTransform` + `Parent` / `Child` system |

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

## What Trecs has that Unity ECS doesn't

- **Template system** — explicit entity blueprints with inheritance
- **Tag-based groups** — entities grouped by identity, not just component set
- **Built-in recording / playback** — with checksum validation and snapshot seeking
- **Deterministic RNG** — framework-level `Rng` with fork support
- **Input system** — frame-isolated input queuing for replay
- **Interpolation** — built-in fixed-to-variable timestep smoothing
- **Sets** — sparse entity subsets without group changes
- **Heap system** — managed/native pointer types for non-component data
- **Reactive entity lifecycle events** — first-class `OnAdded` / `OnRemoved` subscriptions
- **Versioned save format support** — `Reader.Version` / `Writer.Version` for evolving custom serializers

## What Unity ECS has that Trecs doesn't

- **Runtime shape changes** — adding or removing components on an existing entity. Trecs fixes the component set at the template level; the only runtime structural change is a partition transition between compile-time-declared partitions.
- **Managed components** — class-based components.
- **Unbounded per-entity arrays** — Trecs has [`FixedList<N>`](../advanced/fixed-collections.md) for the bounded case; there's no runtime-growing equivalent of `DynamicBuffer<T>`.
- **Enableable components** — toggle components without structural changes.
- **Shared components** — components shared across entities.
- **Built-in transform hierarchy** — `LocalTransform`, `Parent` / `Child`, and a system maintaining `LocalToWorld`.
- **Subscene authoring + baking** — designer-friendly `MonoBehaviour` authoring.
- **Multi-world roles** — explicit Client / Server / ThinClient world wiring for NetCode.
- **Burst-compiled systems** — struct `ISystem` runs the whole system through Burst.
- **Entity Debugger** — visual inspection tools.
- **Physics integration** — Unity Physics package.
- **Entities Graphics** — GPU instanced rendering.
- **Change filters** — detect component modifications.
