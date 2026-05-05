# 17 — Shared Assets with Stable BlobIds

Storing large immutable assets on the shared heap under caller-authored `BlobId`s so many entities can reference the same data by a content-pipeline-stable ID.

**Source:** `Samples/17_BlobStorage/`

## What It Does

A 6×6 grid of cubes cycles through one of two colour palettes. The palettes are `class ColorPalette { List<Color> Colors }` instances — managed data that can't live in a component — stored once on the shared heap. Each cube holds a 12-byte `SharedPtr<ColorPalette>` handle pointing at its palette. The two palettes are seeded under **stable, hand-authored `BlobId`s**, so the same identifier always refers to the same blob regardless of init-time call order.

## Why Stable BlobIds?

`HeapAccessor.AllocShared(T blob)` (no ID argument) auto-mints a `BlobId` from the world's deterministic RNG. That's fine when your init code is itself deterministic, but it breaks in workflows where startup ordering varies — e.g. assets discovered on disk in non-deterministic order, or content loaded in parallel. Hand-authored IDs side-step that entirely:

```csharp
public static class PaletteIds
{
    public static readonly BlobId Warm = new(1001);
    public static readonly BlobId Cool = new(1002);
}
```

For the simpler "allocate and go" pattern where IDs are auto-minted, see [Sample 10 — Pointers](10-pointers.md). This sample is the content-pipeline variant.

## The Seeder Pattern

A long-lived seeder object allocates each blob once at startup under its stable ID and **holds the resulting `SharedPtr<T>` as a member field**. Without that anchor, the blob's refcount would drop to zero in the window between init and the first entity spawn, and the heap would evict it.

```csharp
public class PaletteSeeder
{
    SharedPtr<ColorPalette> _warm;
    SharedPtr<ColorPalette> _cool;

    public void Initialize(WorldAccessor world)
    {
        // AllocShared(stableId, blob) seeds the blob under a caller-chosen BlobId.
        _warm = world.Heap.AllocShared(PaletteIds.Warm, BuildWarm());
        _cool = world.Heap.AllocShared(PaletteIds.Cool, BuildCool());
    }

    public void Dispose(WorldAccessor world)
    {
        _warm.Dispose(world.Heap);
        _cool.Dispose(world.Heap);
    }
}
```

## Entity-Side Lookup

Entity spawners call `AllocShared(stableId)` — the **lookup-only** overload that doesn't pass a blob. It finds the existing blob by ID, bumps its refcount, and returns a fresh handle:

```csharp
world
    .AddEntity<SampleTags.Swatch>()
    .Set(new Position(pos))
    .Set(new PaletteRef
    {
        Value = world.Heap.AllocShared<ColorPalette>(paletteId),
        CycleSpeed = 0.3f,
    });
```

The component itself is plain and unmanaged — the `SharedPtr<T>` is a 12-byte value type:

```csharp
public partial struct PaletteRef : IEntityComponent
{
    public SharedPtr<ColorPalette> Value;
    public float CycleSpeed;
}
```

## Reading the Blob from a System

Systems dereference the handle through the heap accessor — same as any `SharedPtr<T>`:

```csharp
public partial class PaletteCycleSystem : ISystem
{
    [ForEachEntity(Tag = typeof(SampleTags.Swatch))]
    void Execute(in PaletteRef palette, ref ColorComponent color)
    {
        var table = palette.Value.Get(World.Heap);
        // ... sample palette over time, write to ColorComponent
    }
}
```

Because the blob lives once in the shared heap, all 36 entities pointing at the same palette see identical data — and changing the palette (if you adapted this to a mutable case) would update every entity in one write.

## Cleanup Discipline

Pointers stored on components must be disposed when the entity is removed — the framework does **not** auto-dispose, because there's no way for it to know whether you copied the handle elsewhere. This sample doesn't remove entities once spawned, so no cleanup observer is needed in the example code.

If you adapt the pattern to entities that come and go, register an `OnRemoved` observer as shown in [Sample 10 — Pointers](10-pointers.md), or follow the template in [Heap Allocation Rules](../advanced/heap-allocation-rules.md).

## When to Reach for This

- Large, immutable assets that many entities share (colour palettes, lookup tables, mesh metadata, spline definitions, AI behaviour trees).
- Content pipelines where the blob's identity must survive across runs, recordings, or snapshots — and auto-minted IDs would drift.
- Anywhere the data is too big or too managed (lists, dictionaries) to copy into each entity's component.

For per-entity managed data that isn't shared, use `UniquePtr<T>` instead ([Sample 10](10-pointers.md)).

## Concepts Introduced

- **Stable `BlobId`** — caller-authored identifiers that keep the same identity across runs, independent of init-time ordering
- **Seeder pattern** — a long-lived object that allocates shared blobs at startup and anchors their lifetime
- **`AllocShared(id, blob)` vs `AllocShared(id)`** — seeding (two-arg) vs lookup (one-arg) on the same stable ID
- **Cleanup ownership** — pointers on components must be disposed explicitly; an `OnRemoved` observer is the canonical location
