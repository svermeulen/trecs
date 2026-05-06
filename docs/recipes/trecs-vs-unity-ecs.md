# Trecs vs Unity ECS

A comparison for developers familiar with Unity's built-in ECS (Entities package).

Trecs has a deliberately small API surface — a handful of core concepts you can hold in your head, with source generation doing the heavy lifting. Unity ECS covers much more ground and exposes a much larger surface to match: multiple system base types, several iteration patterns, several component flavors, the baking pipeline, NetCode, and more. For the simulation-focused workloads Trecs is built around, the smaller surface is part of the point — less to learn, less to get wrong.

## Architecture

| | Trecs | Unity ECS |
|---|---|---|
| **Storage** | Group-based (entities grouped by tag combination) | Archetype-based (entities grouped by component set) |
| **Memory layout** | Structure-of-arrays: one contiguous buffer per component per group, all indexed by the entity's position in the group | Fixed-size 16 KB chunks per archetype; entities split across many chunks |
| **Entity identity** | `EntityHandle` (stable) + `EntityIndex` (transient) | `Entity` (stable, wraps index + version) |
| **Definition** | Templates (`ITemplate` + `IHasTags`) | No explicit templates (archetype emerges from components) |
| **Structural changes** | Deferred, applied at submission | Deferred via `EntityCommandBuffer` |
| **Multi-world** | Multiple `World` instances supported; no built-in roles or cross-world bridging | Explicit Client / Server / ThinClient worlds, wired into NetCode for Entities |

## Key Differences

### Components

| Trecs | Unity ECS |
|---|---|
| `IEntityComponent` (unmanaged struct) | `IComponentData` (unmanaged struct) |
| No managed components | `class IComponentData` (managed) |
| [`FixedList<N>`](../advanced/fixed-collections.md) (compile-time bounded, inline in component) | `IBufferElementData` / `DynamicBuffer<T>` (unbounded, separately allocated) |
| Tags via `ITag` (separate from components) | `IComponentData` with no fields acts as tag |
| `[Unwrap]` for single-field components | No equivalent |
| Global state via `World.GlobalComponent<T>()` on a `Globals` template | Singletons via `SystemAPI.GetSingleton<T>()` (any component on a single-entity query) |

### Systems

| Trecs | Unity ECS |
|---|---|
| `ISystem` with `Execute()` | `ISystem` with `OnUpdate()` |
| `[ForEachEntity]` source generation | `SystemAPI.Query<T>()` |
| `[ExecuteAfter]` / `[ExecuteBefore]` | `[UpdateAfter]` / `[UpdateBefore]` |
| Four phases (Input, Fixed, Variable, Late) | System groups (`InitializationSystemGroup`, etc.) |
| User instantiates systems explicitly (`new FooSystem(dep1, dep2)`) and registers them with the world (typically `world.AddSystem(...)` after `Build()`, so constructors can take a live `World`) | Framework discovers and instantiates systems via reflection; setup goes in `OnCreate()` |
| Systems are managed classes; Burst is opt-in per job via `[WrapAsJob]` | Struct `ISystem` can be Burst-compiled wholesale; class `SystemBase` is managed |

### Queries

| Trecs | Unity ECS |
|---|---|
| `World.Query().WithTags<T>()` | `EntityQuery` via `GetEntityQuery()` |
| Aspects (`IAspect` + `IRead`/`IWrite`) | Aspects (`IAspect` + `RefRO`/`RefRW`) |
| `[ForEachEntity]` with tag/component scope | `IJobEntity` with query attributes |
| [Sets](../entity-management/sets.md) for sparse filtering | Enableable components |
| Reactive lifecycle via `World.Events.EntitiesWithTags<T>().OnAdded` / `OnRemoved` | No first-class equivalent — change filters or `EntityCommandBuffer` patterns |

### Jobs

| Trecs | Unity ECS |
|---|---|
| `[WrapAsJob]` + `[ForEachEntity]` | `IJobEntity` |
| `[FromWorld]` auto-wiring | `[ReadOnly]` + manual `GetComponentLookup` |
| `NativeWorldAccessor` for structural ops | `EntityCommandBuffer.ParallelWriter` |
| Sort keys for deterministic ordering | `sortKey` on `EntityCommandBuffer` |

### Determinism & Networking

| Trecs | Unity ECS |
|---|---|
| Core design goal | Nice-to-have |
| Built-in recording/playback | No built-in equivalent |
| Deterministic RNG (`World.Rng`) | No built-in equivalent |
| [Input system](../advanced/input-system.md) with frame isolation for perfect replay | Netcode for Entities (separate package) |
| Checksum validation | No built-in equivalent |

### Serialization

| Trecs | Unity ECS |
|---|---|
| Full world state serialization | Subscene baking (edit-time) |
| Runtime save/load | Limited runtime serialization |
| Stable type IDs (auto-generated from type name) | `TypeIndex` (auto-generated) |
| Delta serialization support | No built-in equivalent |
| Per-save versioning via `Writer.Version` / `Reader.Version` for evolving custom serializers across save format revisions | No canonical equivalent — handled outside the engine |

### Editor Tooling

| Trecs | Unity ECS |
|---|---|
| Minimal (text-based debugging) | Entity Debugger, Hierarchy window |
| No visual entity inspector | Component Inspector |

### Authoring

| Trecs | Unity ECS |
|---|---|
| Programmatic — a composition root constructs the world, registers templates and systems, and spawns entities in code | Subscene + `Baker` — designers author with `MonoBehaviour`s in a Subscene, baked into entities at edit/build time |
| Initial world state is set up by spawn calls during initialization | Initial world state is captured in the Subscene and streamed in at runtime |

### Physics & Rendering

| Trecs | Unity ECS |
|---|---|
| External (via OOP bridge) | Unity Physics (built-in) |
| Rendering is user-owned — bring your own approach (GameObject bridge, `Graphics.RenderMeshInstanced`, indirect draws, etc.) | Entities Graphics package |
| No built-in transform hierarchy — define your own `Position`/`Rotation` components and (if needed) parent/child relationships | Built-in `LocalTransform` + `Parent` / `Child`, with a transform system maintaining `LocalToWorld` |

## Tag-Based Identity (and Templates as Blueprints)

The most consequential architectural difference is that Trecs gives entities an identity axis beyond "which components do they carry". Unity ECS has only the component-set view: an entity's identity is *emergent*, determined by whatever components are attached, and two entities with the same component set are indistinguishable in queries. Trecs supports the same component-schema queries (covered below), and additionally lets entities carry **tags** (plain marker structs implementing `ITag`) that queries can filter on.

Tags are the vocabulary systems use to refer to entities, and they act as a proxy for "entity type" — a tag may map 1:1 to a concrete template, or name an abstract role shared across many templates that inherit from a common base. **Templates** themselves are compile-time blueprints describing an entity's component layout and which tags it carries. The template class is named at definition and at registration (`builder.AddEntityType(EnemyEntity.Template)`), but not at spawn sites: `AddEntity<GameTags.Enemy>()` names the *tag*, and the world looks up the template registered for it. Systems should follow the same convention and refer to entities by tag (or by component schema), never by template class — that's what keeps gameplay code decoupled from concrete entity definitions. See [Templates and Tags: Who Does What](../core/templates.md#templates-and-tags-who-does-what) for the full model.

Querying happens through tags and component schemas, not through template names:

```csharp
// Filter by tag — systems don't know or care which template produced the entity.
[ForEachEntity(typeof(GameTags.Enemy), typeof(GameTags.Alive))]
void Execute(ref Health health, in Position pos) { ... }

// Or filter purely by component schema — Unity-ECS style.
[ForEachEntity(MatchByComponents = true)]
void Execute(ref Position pos, in Velocity vel) { ... }
```

Both styles are first-class. `MatchByComponents = true` (and `Query(...).MatchByComponents()`) iterates every entity that has the requested components regardless of tags — useful for cross-cutting systems like rendering, physics sync, or debug overlays that genuinely don't care *what* an entity is, only that it has a `Position` or a `Renderable`. Tag-scoped queries are useful when the subset you want is a design-level concept rather than a component-schema coincidence.

In practice the extra identity axis buys you several things that matter more than they look on paper:

- **Queries can read like the design when you want.** `[ForEachEntity(typeof(GameTags.Enemy))]` says what you mean. Component-set queries drift: "things that have `Health`, `Position`, and `AIState`" is a brittle stand-in for "enemies" and silently picks up anything else that happens to share those components.
- **Tag queries fail loudly when the shape is wrong.** If you ask for a tag-scoped query and the matching template doesn't declare the components you're reading, Trecs surfaces an error instead of quietly matching nothing. Component-matching ECS has the opposite default: if a system expects `Position` + `Velocity` and an entity is missing one of them, the system just skips it — which is a notoriously common source of "why isn't my system running on this entity?" bugs. Tag-scoped queries turn that silent mismatch into a noisy failure the first time you run the code.
- **Templates are a single source of truth for an entity's shape, at spawn time.** The full component layout, defaults, tags, and partitions for "an Enemy" live in one place. Adding a component to the template is one edit; nobody has to remember to attach it at every spawn site. In archetype-based ECS it's normal for the "right" component set to drift across call sites, and for systems to silently skip those entities.
- **Inheritance via `IExtends<>` composes naturally at the template level.** A `Renderable` base captures "things that get drawn", a `Moveable` base captures "things that integrate velocity", and concrete templates extend both. This is shared *structure*, not runtime polymorphism — the generator still flattens it into an unmanaged layout. Systems never need to know about the inheritance graph; they just ask for the tags or components they care about.
- **You can't accidentally create an ambiguous entity.** Spawning goes through `AddEntity<MyTag>()`, which knows exactly which template to use, which components are required, and which have defaults. You never end up with "an entity that's almost an Enemy but missing one component".
- **Refactoring is safer.** Renaming a component, changing its defaults, or swapping in a new one is a change to a template definition, and the generator surfaces every spawn site that's affected. Systems are decoupled from templates and don't need to change unless the tag or component they query on actually changed.
- **Tags carry meaning into tools and debugging.** Because every entity has identity tags, logs, recordings, and inspectors can name entities by what they are rather than by an opaque component-set fingerprint.

The tradeoff is that you do have to define templates up front for the entities you want to spawn — there's no "just attach a component and see what happens" spawn path. For simulation-focused code where the set of entity kinds is deliberately designed, that discipline is usually a feature, not a cost, and `MatchByComponents` is still there for the systems that genuinely don't care about identity.

## Memory Layout: Flat Buffers vs Chunks

The physical storage also differs. Unity ECS splits each archetype across many **fixed-size 16 KB chunks**; entities in the same archetype are spread across however many chunks they fill, and iteration walks chunks in order. Trecs uses a **structure-of-arrays** layout instead: each group owns one flat contiguous buffer *per component* (each one a `NativeList<T>` that grows as the group does), and every entity in the group occupies the same index across all of those buffers. An entity at index `N` in a group has its `Position` at `positionArray[N]`, its `Velocity` at `velocityArray[N]`, and so on — there's no single "entity buffer", just parallel per-component arrays indexed by entity position within the group.

The practical consequences:

- **Iteration is a plain array walk.** `ForEachEntity` over a group is `for (int i = 0; i < count; i++)` against contiguous memory, with no per-chunk bookkeeping. Reading another component of the same entity is one indexed load into the corresponding component array — no chunk lookup or version check.
- **Memory is sized to actual use.** A 16 KB chunk is allocated even when it only holds a handful of entities, so rare archetypes can waste meaningful memory when they proliferate. Trecs only grows each group's buffers as entities are added.
- **Structural changes cost less, and there's less opportunity to do them accidentally.** In Unity ECS, adding or removing any component moves the entity to a different archetype — its data has to be copied to the new archetype's chunks, any previously-held `ComponentLookup` / `RefRW` references can become invalid, and change filters and job dependencies may be affected. It's a foot-gun: users coming from a tag-heavy mindset sometimes reach for add/remove of a zero-sized tag component as a cheap boolean ("is selected", "is on fire"), not realizing it's a full structural change. That's exactly what Unity's **enableable components** exist to fix — a bit-flip instead of an archetype move — but only if you know to use them. In Trecs this foot-gun doesn't exist: a template's component set is fixed at compile time, so you can't add or remove components at runtime at all. The equivalents are partition transitions (explicit, compile-time-declared moves between groups), boolean fields on components (a single write), or sets (membership without touching component storage) — and each of these is visibly what it is at the call site.
- **Parallelism shape is different.** Both engines run on Unity's job system. Unity ECS naturally parallelizes a chunk per worker. Trecs slices a query's matching entities across `IJobParallelFor` batches. The practical throughput is comparable for large workloads; the differences show up only at the extremes — Unity ECS pays per-chunk overhead even on tiny chunks, and Trecs has to slice across multiple groups when a query matches more than one.

Neither layout is universally better — chunk-based storage shines when you have many small archetypes and aggressive per-chunk filtering; flat per-group buffers shine when the set of entity kinds is stable and queries are tag-scoped.

## What Trecs Has That Unity ECS Doesn't

- **Template system** — explicit entity blueprints with inheritance
- **Tag-based groups** — entities grouped by identity, not just component set
- **Built-in recording/playback** — with checksum validation and snapshot seeking
- **Deterministic RNG** — framework-level `Rng` with fork support
- **Input system** — frame-isolated input queuing for replay
- **Interpolation** — built-in fixed-to-variable timestep smoothing
- **Sets** — sparse entity subsets without group changes
- **Heap system** — managed/native pointer types for non-component data
- **Reactive entity lifecycle events** — first-class `OnAdded` / `OnRemoved` subscriptions on tag-scoped queries
- **Versioned save format support** — `Reader.Version` / `Writer.Version` for evolving custom serializers across save revisions

## What Unity ECS Has That Trecs Doesn't

- **Runtime shape changes** — adding or removing components on an existing entity to morph its archetype. Trecs fixes the component set at the template level; the only runtime structural change is a partition transition between compile-time-declared partitions.
- **Managed components** — class-based components
- **Unbounded per-entity arrays** — Trecs covers the bounded case with [`FixedList<N>`](../advanced/fixed-collections.md) (compile-time capacity, stored inline in the component); there's no runtime-growing equivalent of Unity's `DynamicBuffer<T>`
- **Enableable components** — toggle components without structural changes
- **Shared components** — components shared across entities
- **Built-in transform hierarchy** — `LocalTransform`, `Parent`/`Child`, and a system that maintains `LocalToWorld`
- **Subscene authoring + baking** — designer-friendly `MonoBehaviour` authoring converted to entity data at edit/build time
- **Multi-world roles** — explicit Client / Server / ThinClient world wiring for NetCode for Entities
- **Burst-compiled systems** — struct `ISystem` runs the whole system through Burst
- **Entity Debugger** — visual inspection tools
- **Physics integration** — Unity Physics package
- **Entities Graphics** — GPU instanced rendering
- **Change filters** — detect component modifications

