# 14 — Blob Seed Pattern

Store large immutable assets on the shared heap under caller-authored `BlobId`s so many entities can reference the same data by a content-pipeline-stable ID.

**Source:** `com.trecs.core/Samples~/Tutorials/14_BlobSeedPattern/`

## What it does

A 6×6 grid of cubes cycles through one of two colour palettes. Each palette is a `class ColorPalette { List<Color> Colors }` — managed data that can't live in a component — stored once on the shared heap. Each cube holds a 12-byte `SharedPtr<ColorPalette>` pointing at its palette. The two palettes are seeded under **stable, hand-authored `BlobId`s**, so the same identifier always refers to the same blob regardless of init-time call order.

## Why stable BlobIds?

Every `SharedPtr.Alloc` / `NativeSharedPtr.Alloc` requires a caller-supplied `BlobId`. Shared blobs are addressed by stable identifier so independent call sites resolve to the same allocation, and so the same identity survives snapshots, recordings, and re-load. Hand-author the IDs as named constants:

```csharp
public static class PaletteIds
{
    public static readonly BlobId Warm = new(1001);
    public static readonly BlobId Cool = new(1002);
}
```

For the "allocate and go" pattern where IDs are auto-minted, see [Sample 10 — Dynamic Collections](10-pointers.md). This sample is the content-pipeline variant.

## The seeder pattern

A long-lived seeder allocates each blob once at startup under its stable ID and **holds the resulting `BlobPtr<T>` as a member field**. Without that anchor, the cache could evict the blob between init and the first entity spawn.

```csharp
public class PaletteSeeder
{
    readonly BlobCache _blobCache;
    BlobPtr<ColorPalette> _warm;
    BlobPtr<ColorPalette> _cool;

    public PaletteSeeder(BlobCache blobCache) => _blobCache = blobCache;

    public void Initialize()
    {
        // BlobPtr.Alloc(cache, stableId, blob) seeds the blob under a caller-chosen
        // BlobId and returns a pinning handle. Later SharedPtr.Acquire(heap, stableId)
        // calls find the seeded blob and hand out ECS-refcounted handles to it.
        _warm = BlobPtr.Alloc(_blobCache, PaletteIds.Warm, BuildWarm());
        _cool = BlobPtr.Alloc(_blobCache, PaletteIds.Cool, BuildCool());
    }

    public void Dispose()
    {
        _warm.Dispose(_blobCache);
        _cool.Dispose(_blobCache);
    }
}
```

## Entity-side lookup

Entity spawners call `SharedPtr.Acquire<T>(world, stableId)` — the **lookup-only** path. It finds the existing blob by ID, bumps its refcount, and returns a fresh handle:

```csharp
world
    .AddEntity<SampleTags.Swatch>()
    .Set(new Position(pos))
    .Set(new PaletteRef
    {
        Value = SharedPtr.Acquire<ColorPalette>(world, paletteId),
        CycleSpeed = 0.3f,
    });
```

The component is plain and unmanaged — `SharedPtr<T>` is a 12-byte value type:

```csharp
public partial struct PaletteRef : IEntityComponent
{
    public SharedPtr<ColorPalette> Value;
    public float CycleSpeed;
}
```

## Reading the blob from a system

Systems dereference the handle through `WorldAccessor`, like any `SharedPtr<T>`:

```csharp
public partial class PaletteCycleSystem : ISystem
{
    [ForEachEntity(typeof(SampleTags.Swatch))]
    void Execute(in PaletteRef palette, ref ColorComponent color)
    {
        var table = palette.Value.Get(World);
        // ... sample palette over time, write to ColorComponent
    }
}
```

Because the blob lives once in the shared heap, all 36 entities pointing at the same palette see identical data — and a mutable variant would update every entity in one write.

## Cleanup discipline

Pointers on components must be disposed when the entity is removed — the framework does **not** auto-dispose, because it can't know whether you copied the handle elsewhere. This sample registers an `OnRemoved` observer to dispose each entity's `SharedPtr<ColorPalette>` handle when the entity is removed:

```csharp
world
    .Events.EntitiesWithTags<SampleTags.Swatch>()
    .OnRemoved(OnSwatchRemoved)
    .AddTo(_subscriptions);

[ForEachEntity]
void OnSwatchRemoved(in PaletteRef palette)
{
    palette.Value.Dispose(_accessor);
}
```

See also [Pointers — cleanup is manual](../experimental/pointers.md#cleanup-is-manual-for-entity-owned-pointers), [Sample 10 — Dynamic Collections](10-pointers.md), and [Shared Heap Data](../experimental/shared-heap-data.md) for the sharing patterns this sample illustrates.

## When to reach for this

- Large, immutable assets that many entities share (colour palettes, lookup tables, mesh metadata, spline definitions, AI behaviour trees).
- Content pipelines where the blob's identity must survive across runs, recordings, or snapshots — auto-minted IDs would drift.
- Data too big or too managed (lists, dictionaries) to copy into each entity's component.

For per-entity managed data that isn't shared, use `UniquePtr<T>` instead ([Sample 10](10-pointers.md)).

## Concepts introduced

- **Stable `BlobId`** — caller-authored identifiers that keep the same identity across runs, independent of init-time ordering
- **Seeder pattern** — a long-lived object allocates shared blobs at startup via `BlobPtr.Alloc(cache, id, value)` and anchors their lifetime
- **`BlobPtr.Alloc(cache, id, value)` vs `SharedPtr.Acquire(world, id)`** — seeding (creates the blob and returns a pinning handle) vs lookup (acquires an ECS-refcounted handle to an already-seeded blob)
- **Cleanup ownership** — pointers on components must be disposed explicitly; an `OnRemoved` observer is the canonical place
