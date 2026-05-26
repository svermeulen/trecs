# 18 — Heightmap Blobs (Content-Derived BlobIds)

Demonstrates the **content-derived `BlobId`** pattern: hash a descriptor of
what you want to compute, look it up in the world's blob cache, and only
do the work on a cache miss. Four flavors side by side — managed
`SharedPtr<T>` (both class-route and interface-route) sampled main-thread,
and two `NativeSharedPtr<T>` variants sampled inside a Burst-compiled
`[WrapAsJob]` static method.

For the simpler hand-authored-BlobId story (palette assets keyed by
`new BlobId(1001)`), see [Sample 15 — Blob Seed Pattern](../15_BlobSeedPattern/README.md).
This sample is the content-recipe variant: instead of choosing an ID up
front, you derive one from the inputs that produced the data.

## The pattern

1. Define a `HeightmapDescriptor` capturing every input that determines the
   blob's content (resolution, world size, max height, seed).
2. Register a `BlitSerializer<HeightmapDescriptor>` on the world so
   `UniqueHashGenerator` can serialize and hash it.
3. At init time, hash the descriptor → `BlobId`.
4. Call `SharedPtr.GetOrAlloc(heap, blobId, factory)` /
   `NativeSharedPtr.TryGet(...)` first; the heightmap builder only runs
   when the cache doesn't already have the answer.

```csharp
// Same recipe ⇒ same hash ⇒ same BlobId ⇒ same cached blob.
var blobId = new BlobId(_hashGenerator.Generate(descriptor));

_managedAnchor = SharedPtr.GetOrAlloc(
    world,
    blobId,
    () => new HeightmapData(
        descriptor,
        HeightmapBuilder.BuildManagedHeights(descriptor)));
```

Change any field on the descriptor and the hash changes — the cache
naturally invalidates, the factory runs again under the new id.

## Four flavors

The `Flavor` field on `SampleSettings` (or `FlavorOverride` from tests)
selects which path to demonstrate:

- **`ManagedSharedPtr`** — `HeightmapData` is a managed class holding a
  `float[]`, lives on the world's shared heap behind a 12-byte
  `SharedPtr<HeightmapData>` component. The class is marked `[Immutable]`
  and structurally audited by TRECS126 (class adoption path).
  `ManagedHeightmapFollower` runs on the main thread and dereferences
  via `heightmap.Value.Get(World)`.

- **`NativeSharedPtrInline`** — `NativeHeightmapData` is an unmanaged
  struct with the height grid inline (`FixedArray256<float>`), behind a
  `NativeSharedPtr<NativeHeightmapData>` component. `NativeHeightmapFollower`
  is a `[WrapAsJob]` Burst job that resolves the pointer via
  `NativeWorldAccessor.SharedPtrResolver` and reads the heights inline.
  Capped at 256 cells by the inline storage.

- **`NativeSharedPtrTakingOwnership`** — `NativeHeightmapDataLarge` is a
  header struct (descriptor + `BlobArray<float> Heights`) whose heights
  live in the trailing region of the same allocation. Seeded via
  `BlobBuilder` + `NativeSharedPtr.AllocTakingOwnership`: no inline cap,
  no intermediate stack-to-field copy. Same Burst-job read path as the
  inline variant.

- **`ManagedSharedPtrInterface`** — `MutableHeightmapData` is a mutable
  managed class (public fields, populated via an object initializer),
  exposed to entity-side callers through the `[Immutable]`
  `IReadOnlyHeightmapData` interface (interface adoption path). The
  `SharedPtr<IReadOnlyHeightmapData>` handle is the same 12-byte shape
  as `ManagedSharedPtr`; only the type parameter differs.
  `InterfaceHeightmapFollower` reads through the interface on the main
  thread — the underlying concrete's mutable surface is unreachable
  without an explicit downcast.

In all four flavors, every character entity holds its own 12-byte handle
but all handles point at the **same** underlying blob — the heightmap
data lives once on the heap regardless of character count.

## What you see

A green wireframe-style surface plus a row of orange spheres. Each
character wanders smoothly across the XZ plane via 2D Perlin noise
(`CharacterMover`); the per-flavor heightmap follower then samples the
shared blob and writes the resulting Y back to `Position`. The result:
characters slide over the bumps of the surface they're walking on.

## When to reach for this

- The blob is expensive to build but is a pure function of a small set of
  inputs (cave geometry, navmesh, level layouts, baked AI behaviour, etc).
- You want different runs to share the result automatically when the
  inputs match — no caller has to remember to choose a stable id by hand.
- The same blob may be requested by many independent subsystems; you
  want them to converge on one slot in the cache without coordination.

For "I know up front this is `Warm` and that one is `Cool`", use
hand-authored `BlobId` constants — [Sample 15](../15_BlobSeedPattern/README.md).
For per-entity mutable data, use `UniquePtr<T>` —
[Sample 10 — Pointers](../10_Pointers/README.md).

## Cleanup discipline

Same as Sample 15: the scene initializer holds the seeder anchor as a
member and disposes it explicitly. Entity-owned handles aren't disposed
in this sample because no entities are removed during play; if you adapt
the pattern, register an `OnRemoved` observer to dispose each entity's
handle as in [Sample 10](../10_Pointers/README.md).

## Setup (manual)

1. Create a new scene. Add a Camera.
2. Add a GameObject with **Bootstrap** and **HeightmapBlobsCompositionRoot**.
   Drag HeightmapBlobsCompositionRoot into Bootstrap's `CompositionRoot`
   field.
3. Choose a `Flavor` on the composition root's `Settings`.
4. Press Play. The surface mesh appears under the spheres; the spheres
   wander across it, hugging the height.

Documentation: https://svermeulen.github.io/trecs/samples/18-heightmap-blobs/
