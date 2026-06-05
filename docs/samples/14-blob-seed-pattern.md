# 14 — Blob Seed Pattern

Store large immutable assets on the shared heap under caller-authored `BlobId`s so many entities can reference the same data by a content-pipeline-stable ID.

**Source:** `com.trecs.core/Samples~/Tutorials/14_BlobSeedPattern/`

## What it does

A 6×6 grid of cubes cycles through one of two colour palettes. Each palette is a `class ColorPalette { List<Color> Colors }` — managed data that can't live in a component — stored once on the shared heap. Each cube holds a 12-byte `SharedPtr<ColorPalette>` pointing at its palette. The two palettes are seeded under **stable, hand-authored `BlobId`s**, so the same identifier always refers to the same blob regardless of init-time call order.

## Why stable BlobIds?

Shared blobs are addressed by a `BlobId`. For content-pipeline assets like these palettes — where the id is the contract between the seeder and every spawner, and must survive snapshots, recordings, and reload — hand-author stable ids as named constants so independent call sites resolve to the same allocation:

```csharp
public static class PaletteIds
{
    public static readonly BlobId Warm = new(1001);
    public static readonly BlobId Cool = new(1002);
}
```

For the "allocate and go" pattern where IDs are auto-minted, see [Sample 10 — Dynamic Collections](10-dynamic-collections.md). This sample is the content-pipeline variant.

## The seeder pattern

A long-lived seeder registers each palette once at startup under its stable id and **holds a pinning `SharedAnchor<T>` as a member field**. `SharedAnchor<T>` keeps blob bytes resident *outside* the entity refcount layer — without it, the cache could evict the blob between init and the first entity spawn. Because the seeder is setup code, not a system, it makes its own `Unrestricted` accessor.

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
        _accessor = _world.CreateAccessor(AccessorRole.Unrestricted);

        // Register a builder for each palette under its stable BlobId, then acquire a
        // pinning SharedAnchor so the bytes stay resident. Later SharedPtr.Acquire(world,
        // stableId) calls find the registered blob and hand out ECS-refcounted handles to it.
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

See also [Pointers — cleanup is manual](../experimental/pointers.md#cleanup-is-manual-for-entity-owned-pointers), [Sample 10 — Dynamic Collections](10-dynamic-collections.md), and [Shared Heap Data](../experimental/shared-heap-data.md) for the sharing patterns this sample illustrates.

## When to reach for this

- Large, immutable assets that many entities share (colour palettes, lookup tables, mesh metadata, spline definitions, AI behaviour trees).
- Content pipelines where the blob's identity must survive across runs, recordings, or snapshots — auto-minted IDs would drift.
- Data too big or too managed (lists, dictionaries) to copy into each entity's component.

For per-entity managed data that isn't shared, use `UniquePtr<T>` instead ([Sample 10](10-dynamic-collections.md)).

## Concepts introduced

- **Stable `BlobId`** — caller-authored identifiers that keep the same identity across runs, independent of init-time ordering
- **Seeder pattern** — a long-lived object registers shared blobs at startup and holds a pinning `SharedAnchor<T>` to keep them resident before any entity references them
- **`SharedAnchor` (seed/pin) vs `SharedPtr` (entity handle)** — the anchor keeps the blob resident outside the ECS refcount layer; entities acquire ECS-refcounted `SharedPtr` handles by the same id
- **Cleanup ownership** — pointers on components must be disposed explicitly; an `OnRemoved` observer is the canonical place
