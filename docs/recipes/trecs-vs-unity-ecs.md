# Trecs vs Unity ECS

A comparison for developers familiar with Unity's built-in ECS (Entities package).

## Architecture

| | Trecs | Unity ECS |
|---|---|---|
| **Storage** | Group-based (entities grouped by tag combination) | Archetype-based (entities grouped by component set) |
| **Entity identity** | `EntityHandle` (stable) + `EntityIndex` (transient) | `Entity` (stable, wraps index + version) |
| **Definition** | Templates (`ITemplate` + `IHasTags`) | No explicit templates (archetype emerges from components) |
| **Structural changes** | Deferred, applied at submission | Deferred via `EntityCommandBuffer` |

## Key Differences

### Components

| Trecs | Unity ECS |
|---|---|
| `IEntityComponent` (unmanaged struct) | `IComponentData` (unmanaged struct) |
| No managed components | `class IComponentData` (managed) |
| No buffer components | `IBufferElementData` (dynamic buffer) |
| Tags via `ITag` (separate from components) | `IComponentData` with no fields acts as tag |
| `[Unwrap]` for single-field components | No equivalent |

### Systems

| Trecs | Unity ECS |
|---|---|
| `ISystem` with `Execute()` | `ISystem` with `OnUpdate()` |
| `[ForEachEntity]` source generation | `SystemAPI.Query<T>()` |
| `[ExecutesAfter]` / `[ExecutesBefore]` | `[UpdateAfter]` / `[UpdateBefore]` |
| Four phases (Input, Fixed, Variable, Late) | System groups (`InitializationSystemGroup`, etc.) |
| Constructor injection | `OnCreate()` setup |

### Queries

| Trecs | Unity ECS |
|---|---|
| `World.Query().WithTags<T>()` | `EntityQuery` via `GetEntityQuery()` |
| Aspects (`IAspect` + `IRead`/`IWrite`) | Aspects (`IAspect` + `RefRO`/`RefRW`) |
| `[ForEachEntity]` with tag/component scope | `IJobEntity` with query attributes |
| [Sets](../entity-management/sets.md) for sparse filtering | Enableable components |

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

### Editor Tooling

| Trecs | Unity ECS |
|---|---|
| Minimal (text-based debugging) | Entity Debugger, Hierarchy window |
| No visual entity inspector | Component Inspector |

### Physics & Rendering

| Trecs | Unity ECS |
|---|---|
| External (via OOP bridge) | Unity Physics (built-in) |
| GameObjects for rendering | Entities Graphics package |

## What Trecs Has That Unity ECS Doesn't

- **Template system** — explicit entity blueprints with inheritance
- **Tag-based groups** — entities grouped by identity, not just component set
- **Built-in recording/playback** — with checksum validation and bookmark seeking
- **Deterministic RNG** — framework-level `Rng` with fork support
- **Input system** — frame-isolated input queuing for replay
- **Interpolation** — built-in fixed-to-variable timestep smoothing
- **Sets** — sparse entity subsets without group changes
- **Heap system** — managed/native pointer types for non-component data

## What Unity ECS Has That Trecs Doesn't

- **Managed components** — class-based components
- **Dynamic buffers** — variable-length per-entity arrays
- **Enableable components** — toggle components without structural changes
- **Shared components** — components shared across entities
- **Entity Debugger** — visual inspection tools
- **Physics integration** — Unity Physics package
- **Entities Graphics** — GPU instanced rendering
- **Subscene baking** — edit-time entity conversion
- **Change filters** — detect component modifications

## When to Choose Trecs

- You need **deterministic simulation** (networking, replay, competitive games)
- You want **recording and playback** for debugging or replays
- You prefer **explicit templates** over emergent archetypes
- Your project is **simulation-focused** with a clear separation from rendering

## When to Choose Unity ECS

- You need **deep Unity integration** (physics, rendering, editor tooling)
- You're building a **rendering-heavy** project that benefits from Entities Graphics
- You want **managed components** for prototyping flexibility
- You need **dynamic buffers** for variable-length entity data
