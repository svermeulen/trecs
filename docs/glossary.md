# Glossary

Trecs uses several similar-sounding terms with distinct meanings. Each entry links to the page that covers it in depth.

## Identifying entities

| Term | What it is |
|---|---|
| **[Entity](core/entities.md)** | An identifier that groups components. Has no data of its own. |
| **[`EntityHandle`](core/entities.md#entityhandle)** | A *stable* reference to an entity that survives structural changes. Use whenever you store a reference to another entity. |
| **[`EntityAccessor`](core/entities.md#accessing-entity-data)** | A live single-entity view bound to a `WorldAccessor`. Exposes `Get<T>()` plus no-arg `Remove()` / `SetTag<T>()` / `UnsetTag<T>()` / set / input ops on the bound entity. |

## Classifying entities

| Term | What it is |
|---|---|
| **[Tag](core/tags.md)** | A zero-cost marker struct (`ITag`) that classifies entities. Used in template definitions and as a query filter. |
| **[`Tag<T>`](advanced/groups-and-tagsets.md#tagt)** | Runtime value/handle for a single tag type. |
| **[`TagSet`](advanced/groups-and-tagsets.md#tagset)** | Stable identity for a tag combination — a 32-bit hash of the tag GUIDs. Portable across runs and serializable. |
| **[Template](core/templates.md)** | Compile-time blueprint declaring an entity's tags, components, partitions, and inheritance. |

## Where entities live in memory

| Term | What it is |
|---|---|
| **[Group](advanced/groups-and-tagsets.md#groups)** | A unique tag combination plus the contiguous memory block holding entities that carry it. Created implicitly. |
| **[`GroupIndex`](advanced/groups-and-tagsets.md#groupindex)** | `ushort` runtime handle for a group, valid only for one `World`'s lifetime. |
| **[Partition](core/templates.md#partitions)** | A group an entity *moves between* at runtime via `SetTag<T>()` / `UnsetTag<T>()`, for cache locality across entities sharing dynamic state. Partitions an entity moves between share the same template and component types. |
| **[Set](entity-management/sets.md)** | A dynamic membership flag on entities (`IEntitySet`), independent of tags and groups. Iteration visits only members — efficient sparse iteration across groups. |

## Bundling component access

| Term | What it is |
|---|---|
| **[Component](core/components.md)** | Unmanaged struct (`IEntityComponent`) holding per-entity data. |
| **[Aspect](data-access/aspects.md)** | A `partial struct` (`IAspect` + `IRead<>` / `IWrite<>`) bundling related component access into one reusable view. |
| **[Aspect interface](advanced/aspect-interfaces.md)** | A `partial interface` extending `IAspect`, for polymorphic helpers across multiple aspects sharing the same component shape. |

## Touching the world

| Term | What it is |
|---|---|
| **[`World`](core/world-setup.md)** | Container that owns all entities, components, and systems. Drives the per-frame update. |
| **[`WorldAccessor`](core/world-setup.md#worldaccessor)** | Main-thread API for a world (read components, query, queue structural changes). Tagged with an `AccessorRole`. |
| **[`AccessorRole`](advanced/accessor-roles.md)** | `Fixed` / `Variable` / `Unrestricted` — controls what an accessor can do. System-owned accessors get the right role from their phase. |
| **[`NativeWorldAccessor`](performance/jobs-and-burst.md#nativeworldaccessor)** | Burst-compatible counterpart to `WorldAccessor` for use inside jobs. |

## Running logic

| Term | What it is |
|---|---|
| **[System](core/systems.md)** | Class implementing `ISystem` whose `Execute()` runs each frame. |
| **[Phase](core/systems.md#update-phases)** | One of `EarlyPresentation` / `Input` / `Fixed` / `Presentation` / `LatePresentation` — controls when a system runs and what role it gets. |
| **[`[ForEachEntity]`](core/systems.md#foreachentity)** | Marks a method for source-generated entity iteration. |
| **[`[WrapAsJob]`](performance/jobs-and-burst.md)** | Turns a `[ForEachEntity]` method into a Burst-compiled parallel job. |
| **[`[FromWorld]`](advanced/advanced-jobs.md#fromworld--auto-wiring-job-fields)** | Auto-populates fields on a hand-written job struct. |

## Lifetime mechanics

| Term | What it is |
|---|---|
| **[Submission](entity-management/structural-changes.md)** | The point in the frame where queued structural ops are applied. Add / remove / partition transitions are deferred until submission. |
| **[Heap](advanced/heap.md)** | Storage for managed or unmanaged data outside the component buffer, accessed via `SharedPtr` / `UniquePtr` / native variants. |
| **[`BlobId`](advanced/shared-heap-data.md#pattern-b--look-up-by-stable-blobid)** | Stable identifier for a heap blob. Auto-minted from a deterministic RNG, or supplied explicitly when init isn't deterministic. |

## Quick mental model

- **Tags** describe an entity's identity
- **Groups** are the contiguous memory blocks entities live in — one per unique tag combination.
- **Partitions** are groups that double as runtime state (entities move between them).
- **TagSets** are portable handles naming a tag combination.
- **Sets** are ad-hoc subsets you maintain yourself.
- **Aspects** are reusable bundles of read/write component access.
- **Templates** are the compile-time blueprint; **tags** are how runtime code refers to entities.
