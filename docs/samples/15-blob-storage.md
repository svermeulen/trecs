# 15 — Shared Assets with Stable BlobIds

Store large immutable assets on the shared heap under caller-authored `BlobId`s so many entities can reference the same data by a content-pipeline-stable ID.

**Source:** `com.trecs.core/Samples~/Tutorials/15_BlobStorage/`

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

For the "allocate and go" pattern where IDs are auto-minted, see [Sample 10 — Pointers](10-pointers.md). This sample is the content-pipeline variant.

## The seeder pattern

A long-lived seeder allocates each blob once at startup under its stable ID and **holds the resulting `SharedPtr<T>` as a member field**. Without that anchor, the refcount would drop to zero between init and the first entity spawn, and the heap would evict the blob.

```csharp
public class PaletteSeeder
{
    readonly World _world;
    SharedPtr<ColorPalette> _warm;
    SharedPtr<ColorPalette> _cool;

    public PaletteSeeder(World world) => _world = world;

    public void Initialize()
    {
        var world = _world.CreateAccessor(AccessorRole.Unrestricted);
        // SharedPtr.Alloc(heap, stableId, blob) seeds the blob under a caller-chosen BlobId.
        // Subsequent SharedPtr.Alloc(heap, stableId) calls (no value) return new handles to it.
        _warm = SharedPtr.Alloc(world.Heap, PaletteIds.Warm, BuildWarm());
        _cool = SharedPtr.Alloc(world.Heap, PaletteIds.Cool, BuildCool());
    }

    public void Dispose()
    {
        var world = _world.CreateAccessor(AccessorRole.Unrestricted);
        _warm.Dispose(world.Heap);
        _cool.Dispose(world.Heap);
    }
}
```

## Entity-side lookup

Entity spawners call `SharedPtr.Alloc<T>(world.Heap, stableId)` — the **lookup-only** overload (no value argument). It finds the existing blob by ID, bumps its refcount, and returns a fresh handle:

```csharp
world
    .AddEntity<SampleTags.Swatch>()
    .Set(new Position(pos))
    .Set(new PaletteRef
    {
        Value = SharedPtr.Alloc<ColorPalette>(world.Heap, paletteId),
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

Systems dereference the handle through the heap accessor, like any `SharedPtr<T>`:

```csharp
public partial class PaletteCycleSystem : ISystem
{
    [ForEachEntity(typeof(SampleTags.Swatch))]
    void Execute(in PaletteRef palette, ref ColorComponent color)
    {
        var table = palette.Value.Get(World.Heap);
        // ... sample palette over time, write to ColorComponent
    }
}
```

Because the blob lives once in the shared heap, all 36 entities pointing at the same palette see identical data — and a mutable variant would update every entity in one write.

## Cleanup discipline

Pointers on components must be disposed when the entity is removed — the framework does **not** auto-dispose, because it can't know whether you copied the handle elsewhere. This sample never removes entities, so no cleanup observer is needed.

For entities that come and go, register an `OnRemoved` observer as in [Sample 10 — Pointers](10-pointers.md). See also [Heap — cleanup is manual](../advanced/heap.md#cleanup-is-manual-for-entity-owned-pointers) and [Shared Heap Data](../advanced/shared-heap-data.md) for the sharing patterns this sample illustrates.

## When to reach for this

- Large, immutable assets that many entities share (colour palettes, lookup tables, mesh metadata, spline definitions, AI behaviour trees).
- Content pipelines where the blob's identity must survive across runs, recordings, or snapshots — auto-minted IDs would drift.
- Data too big or too managed (lists, dictionaries) to copy into each entity's component.

For per-entity managed data that isn't shared, use `UniquePtr<T>` instead ([Sample 10](10-pointers.md)).

## Concepts introduced

- **Stable `BlobId`** — caller-authored identifiers that keep the same identity across runs, independent of init-time ordering
- **Seeder pattern** — a long-lived object allocates shared blobs at startup and anchors their lifetime
- **`SharedPtr.Alloc(heap, id, value)` vs `SharedPtr.Alloc(heap, id)`** — seeding (three-arg) vs lookup (two-arg) on the same stable ID
- **Cleanup ownership** — pointers on components must be disposed explicitly; an `OnRemoved` observer is the canonical place
