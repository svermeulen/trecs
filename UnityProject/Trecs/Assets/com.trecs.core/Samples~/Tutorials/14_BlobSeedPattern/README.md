# 14 — Blob Seed Pattern

Shows how to seed large, immutable, shared data into the world's shared heap
under stable, content-pipeline-controlled `BlobId`s, and then reference it
from many entities via `SharedPtr<T>`. The blob lives once in the heap
regardless of how many entities point at it, and survives across recordings /
snapshots because the IDs are caller-supplied rather than runtime-generated.

For the simpler "allocate-and-go" `SharedPtr<T>` story (no stable IDs, IDs
auto-minted), see [Sample 10 — Dynamic Collections](../10_DynamicCollections/README.md). This
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

A long-lived "seeder" class registers each blob once at startup with a
stable `BlobId`, acquires a pinning `BlobPtr<T>`, and **holds it as a
member field**. `BlobPtr<T>` is the lower-level pinning handle — it keeps
blob bytes resident without participating in the ECS-side refcount that
`SharedPtr<T>` adds on top. That's the right shape for an anchor that has
to outlive "no entities reference this yet"; without it the cache could
evict the blob between init and the first entity spawn.

```csharp
public class PaletteSeeder
{
    readonly World _world;
    WorldAccessor _accessor;
    BlobPtr<ColorPalette> _warm;
    BlobPtr<ColorPalette> _cool;

    public PaletteSeeder(World world) { _world = world; }

    public void Initialize()
    {
        // Setup code, not a system, so it makes its own Unrestricted accessor.
        _accessor = _world.CreateAccessor(AccessorRole.Unrestricted);
        BlobPtr.Register(_accessor, PaletteIds.Warm, BuildWarm);
        BlobPtr.Register(_accessor, PaletteIds.Cool, BuildCool);
        _warm = BlobPtr.Acquire<ColorPalette>(_accessor, PaletteIds.Warm);
        _cool = BlobPtr.Acquire<ColorPalette>(_accessor, PaletteIds.Cool);
    }
    // Dispose releases the seeder's pins; entity-owned SharedPtrs keep
    // the blob alive until those are disposed too.
}
```

Both layers go through `WorldAccessor`: the seeder pins with `BlobPtr`
(no ECS refcount) while entities reference the same blob with `SharedPtr`
(refcounted). The difference is the *layer*, not the entry point.

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
shown in [Sample 10 — Dynamic Collections](../10_DynamicCollections/README.md) or follow the
template documented in
[Shared Heap Data](../../../../docs/advanced/shared-heap-data.md).

## Setup (manual)

1. Create a new scene. Add a Camera.
2. Add a GameObject with **Bootstrap** and **BlobSeedPatternCompositionRoot**.
   Drag BlobSeedPatternCompositionRoot into Bootstrap's `CompositionRoot` field.
3. Press Play. You should see two interleaved groups of cubes slowly shifting
   through warm and cool palettes on independent cycles.

Documentation: https://svermeulen.github.io/trecs/samples/14-blob-seed-pattern/
