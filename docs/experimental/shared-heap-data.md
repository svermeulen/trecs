# Shared Heap Data

!!! warning "Experimental"
    The `SharedPtr` / `SharedAnchor` / `BlobId` surface this page describes is experimental and may change in future 0.x releases.

Sometimes many entities need the same data — a color palette, a baked navmesh, a shared lookup table. Storing a copy on each entity wastes memory. Instead, allocate the data once on the **heap** and give each entity a lightweight `SharedPtr` handle that points to it. Reference counting frees the data when the last handle is disposed.

A shared blob is a single, immutable value that any number of handles can point at:

1. **Get the blob onto the heap** — by a stable `BlobId`, derived from a descriptor, or content-addressed (the three ways below).
2. **Hand out handles** — each entity stores a `SharedPtr<T>` (or `NativeSharedPtr<T>`) on a component.
3. **Keep it alive at startup** — a seeder holds a pinning [`SharedAnchor`](#seeding) so the blob isn't evicted before any entity references it.
4. **Dispose handles** when entities are removed. When the last handle is gone, the blob is freed.

For the underlying pointer mechanics (`SharedPtr<T>`, `Clone`, `Dispose`), see [Pointers](pointers.md). This page is about how shared blobs are identified, created, and kept alive.

## The three ways to create a shared blob

| Way | You provide | The `BlobId` is | Use it for |
|-----|-------------|-----------------|------------|
| **Named** (`Register` + `Acquire`) | a stable `BlobId` + a value or builder | chosen by you | content-pipeline assets; anything that must resolve by a known id after snapshot reload |
| **Derivable** (`Acquire` from a descriptor) | a small descriptor + a builder registered per descriptor type | derived from the descriptor | data computed from a compact input (a heightmap from a seed, a mesh from parameters) |
| **Content-addressed** (`Alloc`) | the finished blob | hashed from the blob's content | opaque content computed on the fly that you don't want to name |

All three produce the same thing — a refcounted `SharedPtr<T>` to an immutable blob. They differ only in where the `BlobId` comes from.

### Named — `Register` then `Acquire`

Give the blob a stable `BlobId`, register it once at setup, then acquire refcounted handles by that id from anywhere:

```csharp
public static class PaletteIds
{
    public static readonly BlobId Warm = new(0x7f3a9b21d4e6c5a8);
    public static readonly BlobId Cool = new(0x2c5e84f1a097b3d6);
}

// Setup — register a value, or a builder that runs lazily on first acquire.
SharedPtr.Register(world, PaletteIds.Warm, BuildWarm());        // eager value
SharedPtr.Register(world, PaletteIds.Cool, () => BuildCool());  // lazy builder

// Anywhere — acquire a handle by id.
SharedPtr<ColorPalette> warm = SharedPtr.Acquire<ColorPalette>(world, PaletteIds.Warm);

// Safe variant — returns false instead of throwing if nothing is registered there.
if (SharedPtr.TryAcquire<ColorPalette>(world, PaletteIds.Cool, out var cool)) { /* ... */ }
```

This is the path to use when **the id itself is the contract** — an importer bakes an id into level data, or a snapshot serializes the id and the heap must contain a blob under it on reload. `Register` is mirrored on `SharedPtr`, `SharedAnchor`, and their `Native` siblings (it's one shared-heap registry), so call it on whichever type reads best at the site.

### Derivable — `Acquire` from a descriptor

When the blob is *computed from a small input*, register a builder per descriptor type and acquire straight from a descriptor value. Trecs hashes the descriptor to a content-derived `BlobId`, deduplicates against the cache, and runs your builder only on a miss:

```csharp
readonly struct HeightmapDescriptor { public int Seed; public int Resolution; }

// Setup — one builder per descriptor type.
SharedPtr.Register<HeightmapDescriptor, HeightmapData>(world, BuildHeightmap);

// Anywhere — acquire by descriptor; identical descriptors share one blob.
var data = SharedPtr.Acquire<HeightmapDescriptor, HeightmapData>(
    world,
    new HeightmapDescriptor { Seed = 42, Resolution = 256 }
);
```

The builder **must be a pure function of its descriptor** — it must not read mutable world state — because the cache may evict the blob and re-run the builder later. The descriptor type must be registered for serialization (it is hashed to derive the id). Because the descriptor is small and deterministic, this path round-trips through snapshots without persisting the blob's bytes: the recording stores the descriptor and re-derives the blob on load.

### Content-addressed — `Alloc`

For opaque content computed on the fly — a baked result, a background-task output, a payload assembled at runtime — `Alloc` the finished blob and let Trecs derive the id by hashing the content. Identical content automatically deduplicates to one blob, and the id is stable across machines and runs, so you never name it:

```csharp
SharedPtr<BakedMesh> mesh = SharedPtr.Alloc(world, BakeMesh(parameters));

// Native (unmanaged) payloads:
NativeSharedPtr<NavCell> cell = NativeSharedPtr.Alloc(world, in cellValue);
```

Hashing the whole blob isn't free, even on a deduplicating hit, so **`Alloc` once and `Clone` the handle** rather than re-allocing identical content in a loop. If the data is cheaply derivable from a small input, prefer the descriptor path above — it hashes the *descriptor* instead of the whole blob.

!!! note "Opaque blobs and persistence"
    A content-addressed (or otherwise eager) blob has no registered builder to re-derive it, so for it to survive a snapshot / recording reload its bytes must be persisted. Durable saves do this for you when an opaque-blob store is supplied; see [Serialization](serialization.md). Named and derivable blobs don't need this — they re-create from their registration on load.

## Seeding — keeping a blob alive at startup { #seeding }

Eviction can reclaim a blob the moment it has no live handle. A blob registered or alloc'd at startup therefore needs *something* to hold a handle until the first entity references it — otherwise it can be evicted in between. That something is a **seeder** holding a [`SharedAnchor<T>`](pointers.md).

`SharedAnchor<T>` / `NativeSharedAnchor<T>` are dedicated pinning handles for setup and non-ECS code (startup seeders, async preloaders, editor / baking tools). They have the same `Get` / `Clone` / `Dispose` shape as `SharedPtr<T>` and take a `WorldAccessor`; the difference is intent — an anchor exists purely to keep a blob resident, independent of any entity. Store plain `SharedPtr<T>` on components, and hold a `SharedAnchor<T>` in the manager that keeps the blob alive.

```csharp
public class PaletteSeeder
{
    readonly World _world;
    WorldAccessor _accessor;
    SharedAnchor<ColorPalette> _warm;
    SharedAnchor<ColorPalette> _cool;

    public PaletteSeeder(World world) => _world = world;

    public void Initialize()
    {
        // Setup code, not a system, so it makes its own Unrestricted accessor.
        _accessor = _world.CreateAccessor(AccessorRole.Unrestricted);

        // Register each palette under a stable id, then pin it with an anchor.
        SharedAnchor.Register(_accessor, PaletteIds.Warm, BuildWarm);
        SharedAnchor.Register(_accessor, PaletteIds.Cool, BuildCool);
        _warm = SharedAnchor.Acquire<ColorPalette>(_accessor, PaletteIds.Warm);
        _cool = SharedAnchor.Acquire<ColorPalette>(_accessor, PaletteIds.Cool);
    }

    public void Dispose()
    {
        _warm.Dispose(_accessor);
        _cool.Dispose(_accessor);
    }
}
```

Entities then acquire their own handles by the same id — the seeder's anchor and the entity-side refcounts keep the blob alive independently:

```csharp
world.AddEntity<MyTag>()
    .Set(new PaletteRef
    {
        Value = SharedPtr.Acquire<ColorPalette>(world, PaletteIds.Warm),
    });
```

This is the [Blob Seed Pattern](../samples/14-blob-seed-pattern.md) (Sample 14). The anchor covers all three creation paths, so the seeder shape is the same for a derivable or content-addressed blob — pin with `SharedAnchor.Acquire(world, descriptor)` or `SharedAnchor.Alloc(world, blob)` instead of the named `Register` + `Acquire` pair. [Sample 17](../samples/17-heightmap-blobs.md) pins its descriptor-derived heightmaps with `SharedAnchor.Acquire<TDesc,T>` exactly this way.

## Determinism domains { #determinism-domains }

Trecs's fixed update is deterministic — it is snapshotted, checksummed, and replayed from recordings. Blob handles participate in that, and each handle type belongs to a **domain** with its own rules:

| Holder | Domain | Snapshotted? | Allowed from |
|--------|--------|--------------|--------------|
| `SharedPtr` / `NativeSharedPtr` | simulation | yes — refcounts are world state | Fixed / Unrestricted accessors |
| `InputSharedPtr` / `InputNativeSharedPtr` | input | rides the recording's input stream | Input systems |
| `SharedAnchor` / `NativeSharedAnchor` | ambient (rendering, async loaders, seeders, editor tools) | no — invisible to snapshots and replay | Unrestricted / Variable / Input — **throws from Fixed** |

Three rules fall out of this:

**1. Simulation resolves only what's deterministically reachable.** `SharedPtr.Acquire` / `TryAcquire` by id succeed iff the blob is already held by the sim heap or backed by a registration the sim can rely on in every run — a setup-window `Register`, or a descriptor the simulation itself interned. They never succeed just because the bytes happen to be resident (e.g. pinned by someone's anchor): that answer would differ between runs — true in the editor with rendering attached, false in a headless replay — which is exactly the kind of silent desync the rule prevents. `TryAcquire` answering false is therefore a *stable* answer, and `Acquire` fails with an explanatory exception rather than silently working in one environment only.

   The anchor types keep the opposite semantics on purpose: `SharedAnchor.TryAcquire` asks "is this resident *right now*?" — the right primitive for ambient code polling whether an async preload has landed.

**2. Anchors can't be used from fixed-update code — at all.** Every anchor operation through a Fixed-role accessor throws, reads included: an anchor is an ambient hold that no snapshot or replay can reproduce, so simulation code must not depend on one — and `CanGet`/`TryGet`/`IsResident` are residency probes whose answers vary with cache state, which simulation must never branch on. From simulation, use `SharedPtr` (snapshotted), or receive the data through the input stream (rule 3). Anchors also can't be stored on components (TRECS137): their handle is a live cache handle that doesn't survive serialization — components hold `SharedPtr`/`NativeSharedPtr`. Similarly, input pointers can't be stored on persistent components (TRECS136) — convert them (rule 3) in the frame that delivers them.

   One more contract the framework now verifies for you in debug builds: **builders must be pure.** A `Register` factory or descriptor builder runs again whenever the cache re-materializes an evicted blob, so it must produce byte-identical content from the same inputs — no time, RNG, or mutable world state. Debug builds hash blob content and assert identical bytes on every re-materialization, so an impure builder fails loudly at the insert that diverged instead of silently desyncing a replay.

**3. Ambient work products enter the simulation through the input stream.** When non-sim code (an async loader, a background bake) produces data the simulation needs, don't pass a bare `BlobId` across — publish the blob on an `[Input]` component field via `InputSharedPtr.Alloc` (finished bytes) or `InputSharedPtr.Acquire<TDesc,T>` (descriptor), and have the receiving system convert the in-hand pointer:

```csharp
// Input system (ambient side): publish the payload.
var inputPtr = InputSharedPtr.Alloc(world, bakedResult);
handle.AddInput(world, new BakeFinished { Payload = inputPtr });

// Fixed system (sim side): convert the delivered pointer into a sim-owned handle.
SharedPtr<BakedData> simPtr = SharedPtr.Acquire(world, input.Payload);
```

The conversion is recorded with the input, so a replay reproduces the handoff exactly — where a bare id would point at ambient memory the replay doesn't have. Convert in the frame that delivers the payload; the input heap only guarantees the blob's lifetime for that frame.

!!! tip "The seeder pattern is the ergonomic default for 'data everyone needs'"
    Register at setup, pin with an anchor from setup code (an Unrestricted accessor, as in the seeder above), acquire by well-known id from anywhere — including fixed update. The registration is what makes the id deterministically resolvable; the anchor is purely a performance hold that keeps the bytes materialized.

## `[Immutable]` requirement { #two-adoption-paths-for-immutable }

`SharedPtr<T>` requires `T` to carry `[Trecs.Immutable]` (or be on the built-in allowlist — `string`, `Type`, etc.). A shared blob is read by every handle that points at it and is not snapshotted with game state, so any post-creation mutation silently desyncs determinism. The marker makes that a compile-time error instead.

The attribute can go on either a class or an interface:

- **Class route** — `[Immutable] sealed class Foo`. TRECS126 structurally audits the class: every field `readonly`, no public setters, public field/property types in the "obviously immutable" set. Best for **small leaf types built via a single constructor** — palettes, content descriptors, lookup tables.
- **Interface route** — `[Immutable] interface IReadOnlyFoo`. Mark a read-only interface `[Immutable]` and parameterize `SharedPtr<T>` on it. The mutable concrete implements the read members but keeps its existing construction lifecycle. Best for **types that can't be reshaped around field-level immutability** — pool-allocated, deserialized in place, multi-pass builders.

### Interface route — worked example

```csharp
[Trecs.Immutable]
public interface IReadOnlyWorldRegion
{
    int RegionId { get; }
    float Radius { get; }
    IReadOnlyList<Vector3> Waypoints { get; }
    IReadOnlyList<IReadOnlyPortal> Portals { get; }
}

[Trecs.Immutable]
public interface IReadOnlyPortal
{
    Vector3 Position { get; }
    int LinkedRegionId { get; }
}

public sealed class WorldRegion : IReadOnlyWorldRegion
{
    public int RegionId;
    public float Radius;
    public List<Vector3> Waypoints = new();
    public List<Portal> Portals = new();

    int IReadOnlyWorldRegion.RegionId => RegionId;
    float IReadOnlyWorldRegion.Radius => Radius;
    IReadOnlyList<Vector3> IReadOnlyWorldRegion.Waypoints => Waypoints;
    // IReadOnlyList<out T> is covariant — Portal : IReadOnlyPortal
    // lets this upcast work with no copy or allocation.
    IReadOnlyList<IReadOnlyPortal> IReadOnlyWorldRegion.Portals => Portals;
}
```

Acquire `SharedPtr<T>` parameterized on the interface:

```csharp
public partial struct WorldRegionRef : IEntityComponent
{
    public SharedPtr<IReadOnlyWorldRegion> Value;
}
```

The seeder registers the mutable concrete; entity-side reads only ever see the read-only face.

### Safe property types

Public properties on an `[Immutable]` type must return types from the "obviously immutable" set (TRECS126):

- Primitives, `string`, enums
- `readonly struct`s and other `[Immutable]` types (recursively)
- BCL read-only views — `ImmutableArray<T>`, `ImmutableList<T>`, `ReadOnlyMemory<T>`, `ReadOnlySpan<T>`, `ReadOnlyCollection<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IReadOnlyDictionary<TKey, TValue>`, `IReadOnlySet<T>`
- Unity native read-only views — `NativeArray<T>.ReadOnly`, `NativeHashMap<TKey, TValue>.ReadOnly`, `NativeHashSet<T>.ReadOnly`, `NativeParallelHashMap<TKey, TValue>.ReadOnly`, `NativeParallelMultiHashMap<TKey, TValue>.ReadOnly`

Method return types on `[Immutable]` interfaces that aren't in the safe set trigger TRECS127 as a warning. Annotate with `[Trecs.AllowMutableReturn]` when intentional:

```csharp
[Trecs.Immutable]
public interface IReadOnlyWorldRegion
{
    int CellCount { get; }

    [Trecs.AllowMutableReturn]
    Dictionary<int, List<short>> GetCellLookup();
}
```

### Limits

Two cases `[Immutable]` can't enforce:

- **Aliasing.** If the caller keeps a reference to a mutable collection passed into the constructor, they can mutate the blob after construction. Copy inputs or use immutable collection types.
- **Downcasting.** `(WorldRegion)readOnlyView.Get(world)` recovers the mutable surface. Make the concrete `internal` if this matters.

## Choosing `BlobId` values

Only the **named** path needs you to pick a `BlobId` — the derivable and content-addressed paths derive it for you. When you do name a blob, put the value behind a named constant so call sites read `PaletteIds.Warm`:

```csharp
public static readonly BlobId Warm = new(/* ??? */);
```

Options:

- **Random 64-bit literals** — `new(0x7f3a9b21d4e6c5a8)`. Generate once, paste in, never change. Zero collision risk across modules. A reasonable default.
- **Domain keys** — `BlobIdGenerator.FromKey(42)`. Wraps a `long` with a zero-check guard. Convenient when you already have a numeric domain identifier.
- **Stable string hashes** — `BlobIdGenerator.FromBytes(Encoding.UTF8.GetBytes("warm-palette"))`. Derivable from the name; useful when ids round-trip through text formats.
- **Asset-pipeline ids** — GUIDs or content hashes from an importer, cast or hashed to `long`.
- **Hand-assigned small ints** — `new(1001)`, `new(1002)`, etc. Simplest for a single codebase, but risks collision in multi-module setups.

!!! tip
    Reusing one `BlobId` for two genuinely different blobs aliases them — both call sites resolve to whichever was stored first. A DEBUG build asserts on this the moment the id is reused for different content. Random literals or derived ids avoid the hazard entirely; content-addressing makes it impossible.
