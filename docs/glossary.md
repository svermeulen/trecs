# Glossary

Trecs uses several similar-sounding terms with distinct meanings. This page is a one-stop cross-reference. Each entry links to the page where the concept is explained in depth.

## Identifying entities

| Term | What it is |
|---|---|
| **[Entity](core/entities.md)** | An identifier that groups components together. Has no data of its own. |
| **[`EntityHandle`](core/entities.md#entityhandle-vs-entityindex)** | A *stable* reference to an entity that survives structural changes. Use for long-lived references. |
| **[`EntityIndex`](core/entities.md#entityhandle-vs-entityindex)** | A *transient* reference that points directly into the underlying buffers. Faster than `EntityHandle` but invalidated by any structural change. |
| **[`EntityAccessor`](core/entities.md#accessing-entity-data)** | A single-entity convenience view exposing `Get<T>()` for component access. |

## Classifying entities

| Term | What it is |
|---|---|
| **[Tag](core/tags.md)** | A zero-cost marker struct (`ITag`) that classifies entities. Used at template definition time and as query filter. |
| **[`Tag<T>`](advanced/groups-and-tagsets.md#tagt)** | The runtime value/handle for a single tag type. |
| **[`TagSet`](advanced/groups-and-tagsets.md#tagset)** | A stable identity for a tag combination — a 32-bit hash of the tag GUIDs. Portable across runs and serializable. |
| **[Template](core/templates.md)** | A compile-time blueprint declaring an entity's tags, components, partitions, and inheritance. |

## Where entities live in memory

| Term | What it is |
|---|---|
| **[Group](advanced/groups-and-tagsets.md#groups)** | One tag combination from a template, paired with the contiguous memory block holding every entity that carries that combination. Created implicitly. |
| **[`GroupIndex`](advanced/groups-and-tagsets.md#groupindex)** | A small `ushort` runtime handle for a group, valid only for the lifetime of one `World`. |
| **[Partition](core/templates.md#partitions)** | A group an entity *moves between* at runtime, to improve cache locality for entities that share dynamic state. Each partition that an entity moves between is based on the same template and therefore has the same component types. |
| **[Set](entity-management/sets.md)** | A dynamic membership flag on entities (`IEntitySet`). Independent of tags and groups; iteration visits only members. Allows for efficient sparse iteration over entities across groups. |

## Bundling component access

| Term | What it is |
|---|---|
| **[Component](core/components.md)** | An unmanaged struct (`IEntityComponent`) holding per-entity data. |
| **[Aspect](data-access/aspects.md)** | A `partial struct` (with interfaces `IAspect` + `IRead<>` / `IWrite<>`) that bundles related component access into one reusable view. |
| **[Aspect interface](advanced/aspect-interfaces.md)** | A `partial interface` extending `IAspect` for polymorphic helpers across multiple aspects sharing the same component shape. |

## Touching the world

| Term | What it is |
|---|---|
| **[`World`](core/world-setup.md)** | The container that owns all entities, components, and systems. Drives the per-frame update. |
| **[`WorldAccessor`](core/world-setup.md#worldaccessor)** | The primary main-thread API for talking to a world (read components, query, queue structural changes). Tagged with an `AccessorRole`. |
| **[`AccessorRole`](advanced/accessor-roles.md)** | `Fixed` / `Variable` / `Unrestricted` — controls what an accessor is allowed to do. System-owned accessors get the right role automatically from their phase. |
| **[`NativeWorldAccessor`](performance/jobs-and-burst.md#nativeworldaccessor)** | The Burst-compatible counterpart to `WorldAccessor` for use inside jobs. |

## Running logic

| Term | What it is |
|---|---|
| **[System](core/systems.md)** | A class implementing `ISystem` whose `Execute()` runs each frame. |
| **[Phase](core/systems.md#update-phases)** | One of `EarlyPresentation` / `Input` / `Fixed` / `Presentation` / `LatePresentation` — controls when a system runs and what role it gets. |
| **[`[ForEachEntity]`](core/systems.md#foreachentity)** | Attribute that marks a method for source-generated entity iteration. |
| **[`[WrapAsJob]`](performance/jobs-and-burst.md)** | Attribute that turns a `[ForEachEntity]` method into a Burst-compiled parallel job. |
| **[`[FromWorld]`](advanced/advanced-jobs.md#fromworld--auto-wiring-job-fields)** | Attribute that auto-populates fields on a hand-written job struct. |

## Lifetime mechanics

| Term | What it is |
|---|---|
| **[Submission](entity-management/structural-changes.md)** | The point in the frame where queued structural ops are applied. Add / remove / move are deferred until submission. |
| **[Heap](advanced/heap.md)** | Storage for managed or unmanaged data outside the component buffer, accessed via `SharedPtr` / `UniquePtr` / native variants. |
| **[`BlobId`](advanced/heap-allocation-rules.md#stable-blobids-when-init-isnt-deterministic)** | Stable identifier for a heap blob. Auto-minted from a deterministic RNG, or supplied explicitly when init isn't deterministic. |

## Quick mental model

- **Tags** describe an entity's identity
- **Groups** are the contiguous memory blocks entities live in — one per unique tag combination.
- **Partitions** are groups that double as runtime state (entities move between them).
- **TagSets** are portable handles naming a tag combination.
- **Sets** are ad-hoc subsets you maintain yourself.
- **Aspects** are reusable bundles of read/write component access.
- **Templates** are the compile-time blueprint; **tags** are how runtime code refers to entities.
