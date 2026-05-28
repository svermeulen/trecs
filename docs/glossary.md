# Glossary

Trecs uses several similar-sounding terms with distinct meanings. Each entry links to the page that covers it in depth.

## Identifying entities

| Term | What it is |
|---|---|
| **[Entity](core/entities.md)** | An identifier that groups components. Has no data of its own. |
| **[EntityHandle](core/entities.md#entityhandle)** | A *stable* reference to an entity that survives structural changes. Primary user-facing identifier. Carries `Component<T>(world)` / `TryComponent<T>(world, out)` / `Remove(world)` / `SetTag<T>(world)` / `UnsetTag<T>(world)` / `AddInput<T>(world, v)`. `Remove`, `SetTag`, and `UnsetTag` also have `NativeWorldAccessor` overloads for Burst. |
| **[EntityIndex](advanced/entity-index.md)** | (Advanced) A *transient* reference (buffer position within a group). Only valid within the current submission cycle. Same method surface as `EntityHandle` but skips the handle-to-index lookup. Always prefer `EntityHandle` unless profiling identifies the lookup as a bottleneck. |

## Classifying entities

| Term | What it is |
|---|---|
| **[Tag](core/tags.md)** | A zero-cost marker struct (`ITag`) that classifies entities. Used in template definitions and as a query filter. |
| **[TagSet](advanced/groups-and-tagsets.md#tagset)** | Stable identity for a tag combination — a 32-bit interned ID derived from the member `TypeId` values. Portable across runs and serializable. |
| **[Template](core/templates.md)** | Compile-time blueprint declaring an entity's tags, components, partitions, and inheritance. |
| **[Abstract template](core/templates.md#abstract-templates)** | A template marked with the C# `abstract` keyword. Usable as an `IExtends<>` base but cannot be passed to `WorldBuilder.AddTemplate`. |

## Where entities live in memory

| Term | What it is |
|---|---|
| **[Group](advanced/groups-and-tagsets.md#groups)** | The contiguous memory block holding some number of entities. Created implicitly based on a unique tag combination. |
| **[GroupIndex](advanced/groups-and-tagsets.md#groupindex)** | `ushort` runtime handle for a group, valid only for one `World`'s lifetime. |
| **[Partition](core/templates.md#partitions)** | A group an entity *moves between* at runtime via `SetTag<T>()` / `UnsetTag<T>()`, for cache locality across entities sharing dynamic state. Partitions an entity moves between share the same template and component types. |
| **[Set](entity-management/sets.md)** | A dynamic membership flag on entities, independent of tags and groups. Iteration visits only members. Allows efficient sparse iteration across groups. |

## Bundling component access

| Term | What it is |
|---|---|
| **[Component](core/components.md)** | Unmanaged struct (`IEntityComponent`) holding per-entity data. |
| **[Aspect](data-access/aspects.md)** | A `partial struct` (`IAspect` + `IRead<>` / `IWrite<>`) bundling related component access into one reusable view. |
| **[Aspect interface](advanced/aspect-interfaces.md)** | A `partial interface` extending `IAspect`, for polymorphic helpers across multiple aspects sharing the same component shape. |

## Touching the world

| Term | What it is |
|---|---|
| **[World](core/world-setup.md)** | Container that owns all entities, components, and systems. Drives the per-frame update. |
| **[WorldAccessor](core/world-setup.md#worldaccessor)** | Main-thread API for a world. Tagged with an `AccessorRole` which determines what operations are permitted. |
| **[AccessorRole](advanced/accessor-roles.md)** | `Fixed` / `Variable` / `Unrestricted` — controls what an accessor can do. System-owned accessors get the right role from their phase. |
| **[NativeWorldAccessor](performance/jobs-and-burst.md#nativeworldaccessor)** | Burst-compatible counterpart to `WorldAccessor` for use inside jobs. |

## Running logic

| Term | What it is |
|---|---|
| **[System](core/systems.md)** | Class implementing `ISystem` whose `Execute()` runs each frame. |
| **[Phase](core/systems.md#phase-diagram)** | One of `EarlyPresentation` / `Input` / `Fixed` / `Presentation` / `LatePresentation` — controls when a system runs and what role it gets. |
| **[`[ForEachEntity]`](core/systems.md#foreachentity)** | Marks a method for source-generated entity iteration. |
| **[`[WrapAsJob]`](performance/jobs-and-burst.md)** | Turns a `[ForEachEntity]` method into a Burst-compiled parallel job. |
| **[`[FromWorld]`](advanced/advanced-jobs.md#fromworld-auto-wiring-job-fields)** | Auto-populates fields on a hand-written job struct. |

## Lifetime mechanics

| Term | What it is |
|---|---|
| **[Submission](entity-management/structural-changes.md)** | The point in the frame where queued structural ops are applied. Add / remove / partition transitions are deferred until submission. |
| **[Heap](experimental/pointers.md)** | Storage for managed or unmanaged data outside the component buffer, accessed via `SharedPtr` / `UniquePtr` / native variants. |
| **[BlobId](experimental/shared-heap-data.md#pattern-b-look-up-by-stable-blobid)** | Stable identifier for a heap blob. Supplied explicitly when allocating shared data on heap. |

## Quick mental model

- **[Tags](core/tags.md)** describe an entity's identity
- **[Groups](advanced/groups-and-tagsets.md#groups)** are the contiguous memory blocks entities live in — one per unique tag combination.
- **[Partitions](core/templates.md#partitions)** are groups that double as runtime state (entities move between them so logic runs with maximum cache locality).
- **[TagSets](advanced/groups-and-tagsets.md#tagset)** are portable handles naming a tag combination.
- **[Sets](entity-management/sets.md)** are ad-hoc subsets you maintain yourself.
- **[Aspects](data-access/aspects.md)** are reusable bundles of read/write component access.
- **[Templates](core/templates.md)** are the compile-time blueprint; **[tags](core/tags.md)** are how runtime code refers to entities.
