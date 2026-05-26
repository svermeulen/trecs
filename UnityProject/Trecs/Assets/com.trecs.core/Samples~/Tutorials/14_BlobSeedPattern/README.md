# 15 — Blob Seed Pattern

Shows how to seed large, immutable, shared data into the world's shared heap
under stable, content-pipeline-controlled `BlobId`s, and then reference it
from many entities via `SharedPtr<T>`. The blob lives once in the heap
regardless of how many entities point at it, and survives across recordings /
snapshots because the IDs are caller-supplied rather than runtime-generated.

For the simpler "allocate-and-go" `SharedPtr<T>` story (no stable IDs, IDs
auto-minted), see [Sample 10 — Pointers](../10_Pointers/README.md). This
sample is the content-pipeline variant.

## Why stable BlobIds

`WorldAccessor.AllocShared(T blob)` mints `BlobId`s automatically from the
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
`BlobPtr.Alloc(cache, stableId, blob)` and **holds the resulting
`BlobPtr<T>` as a member field**. `BlobPtr<T>` is the lower-level pinning
handle that lives at the `BlobCache` layer — it keeps blob bytes in the
cache without participating in the ECS-side refcount that `SharedPtr<T>`
adds on top. That's the right shape for an anchor that has to outlive
"no entities reference this yet"; without it the cache could evict the
blob between init and the first entity spawn.

```csharp
public class PaletteSeeder
{
    readonly BlobCache _blobCache;
    BlobPtr<ColorPalette> _warm;
    BlobPtr<ColorPalette> _cool;

    public PaletteSeeder(BlobCache blobCache) { _blobCache = blobCache; }

    public void Initialize()
    {
        _warm = BlobPtr.Alloc(_blobCache, PaletteIds.Warm, BuildWarm());
        _cool = BlobPtr.Alloc(_blobCache, PaletteIds.Cool, BuildCool());
    }
    // Dispose releases the seeder's pins; entity-owned SharedPtrs keep
    // the blob alive until those are disposed too.
}
```

Note the deliberate asymmetry: the seeder takes a `BlobCache` (not a
`World` or `WorldAccessor`) because it isn't an ECS participant — it's a
cache-level pin whose only job is to keep the data resident.

Entity spawners then look up the blob by stable ID via the ECS-side
`SharedPtr` API:

```csharp
.Set(new PaletteRef
{
    Value = SharedPtr.Acquire<ColorPalette>(world, PaletteIds.Warm),
    CycleSpeed = 0.3f,
})
```

`SharedPtr.Acquire` creates a fresh refcounted handle to the existing
blob. The seeder's `BlobPtr` and each entity's `SharedPtr` pin the same
underlying cache entry through independent mechanisms — the blob stays
resident as long as either side holds at least one handle.

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
[Shared Heap Data](../../../../docs/advanced/shared-heap-data.md).

## Setup (manual)

1. Create a new scene. Add a Camera.
2. Add a GameObject with **Bootstrap** and **BlobSeedPatternCompositionRoot**.
   Drag BlobSeedPatternCompositionRoot into Bootstrap's `CompositionRoot` field.
3. Press Play. You should see two interleaved groups of cubes slowly shifting
   through warm and cool palettes on independent cycles.

Documentation: https://svermeulen.github.io/trecs/samples/15-blob-seed-pattern/
