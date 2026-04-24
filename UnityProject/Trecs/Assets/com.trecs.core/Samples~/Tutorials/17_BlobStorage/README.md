# 17 — Shared Assets (Stable BlobIds)

Shows how to seed large, immutable, shared data into the world's shared heap
under stable, content-pipeline-controlled `BlobId`s, and then reference it
from many entities via `SharedPtr<T>`. The blob lives once in the heap
regardless of how many entities point at it, and survives across recordings /
bookmarks because the IDs are caller-supplied rather than runtime-generated.

For the simpler "allocate-and-go" `SharedPtr<T>` story (no stable IDs, IDs
auto-minted), see [Sample 10 — Pointers](../10_Pointers/README.md). This
sample is the content-pipeline variant.

## Why stable BlobIds

`HeapAccessor.AllocShared(T blob)` mints `BlobId`s automatically from the
world's deterministic RNG — fine when init code is itself deterministic, but
can break in workflows where startup ordering varies (e.g. assets discovered
on disk in non-deterministic order). Hand-authored IDs side-step that:

```csharp
public static class PaletteIds
{
    public static readonly BlobId Warm = new(1001);
    public static readonly BlobId Cool = new(1002);
}
```

The same identifier will always refer to the same blob, regardless of which
order things were loaded.

## The seeder pattern

A long-lived "seeder" class allocates each blob once at startup using
`AllocShared(stableId, blob)` and **holds the resulting `SharedPtr<T>` as a
member field**. Without the anchor, the blob's refcount would drop to zero
between init and the first entity spawn and the cache would evict it.

```csharp
public class PaletteSeeder
{
    SharedPtr<ColorPalette> _warm;
    SharedPtr<ColorPalette> _cool;

    public void Initialize(WorldAccessor world)
    {
        _warm = world.Heap.AllocShared(PaletteIds.Warm, BuildWarm());
        _cool = world.Heap.AllocShared(PaletteIds.Cool, BuildCool());
    }
    // Dispose releases the seeder's anchor; entity-owned handles keep the
    // blob alive until those are disposed too.
}
```

Entity spawners then look up the blob by stable ID:

```csharp
.Set(new PaletteRef
{
    Value = world.Heap.AllocShared<ColorPalette>(PaletteIds.Warm),
    CycleSpeed = 0.3f,
})
```

`AllocShared(BlobId)` (the lookup-only overload) creates a fresh handle to
the existing blob and bumps its reference count.

## What the sample does

- A `PaletteSeeder` allocates two `ColorPalette` blobs (managed `class`
  holding a `List<Color>`) under stable BlobIds and holds them as members.
- `SceneInitializer` spawns a 6×6 grid of cubes, each entity referencing one
  of the two palettes via `SharedPtr<ColorPalette>` on its `PaletteRef`
  component.
- A `PaletteCycleSystem` reads each entity's blob each frame and samples the
  palette over time, driving the cube's colour.

## Cleanup discipline

Pointers stored on components must be disposed when the entity is removed —
the framework does **not** auto-dispose. This sample doesn't remove entities
once spawned, so an `OnRemoved` cleanup observer isn't needed in the example
code. If you adapt the pattern to entities that come and go, register one as
shown in [Sample 10 — Pointers](../10_Pointers/README.md) or follow the
template documented in
[Heap Allocation Rules](../../../../docs/advanced/heap-allocation-rules.md).

## Setup (manual)

1. Create a new scene. Add a Camera.
2. Add a GameObject with **Bootstrap** and **BlobStorageCompositionRoot**.
   Drag BlobStorageCompositionRoot into Bootstrap's `CompositionRoot` field.
3. Press Play. You should see two interleaved groups of cubes slowly shifting
   through warm and cool palettes on independent cycles.

Documentation: https://svermeulen.github.io/trecs/samples/17-blob-storage/
