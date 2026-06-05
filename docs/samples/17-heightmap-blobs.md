# 17 — Heightmap Blobs

Derive a shared blob's `BlobId` from a content "recipe" — the exact inputs that produce the data — so independent call sites with the same inputs converge on one cached blob, and the expensive build only runs on a cache miss. Compare with [Sample 14 — Blob Seed Pattern](14-blob-seed-pattern.md), which uses hand-authored stable `BlobId`s instead.

**Source:** `com.trecs.core/Samples~/Tutorials/17_HeightmapBlobs/`

## What it does

A green wireframe-style surface plus a row of orange spheres. Each character wanders smoothly across the XZ plane via 2D Perlin noise; a per-flavor heightmap-follower system reads the shared heightmap blob and writes the resulting Y back to `Position`. The result: characters slide over the bumps of the surface they're walking on.

The same noise function drives both the visual mesh and the runtime sampling, so the visual matches what the characters feel.

## Four flavors, one sample

The `Flavor` field on `SampleSettings` (or `FlavorOverride` from tests) selects which path to demonstrate:

- **`ManagedSharedPtr`** — `HeightmapData` is a managed class holding a `float[]`, lives on the world's shared heap behind a 12-byte `SharedPtr<HeightmapData>` component. The class is marked `[Immutable]` and structurally audited by TRECS126 (class adoption path). `ManagedHeightmapFollower` runs on the main thread and dereferences via `heightmap.Value.Get(World)`.

- **`NativeSharedPtrInline`** — `NativeHeightmapData` is an unmanaged struct with the height grid inline (`FixedArray256<float>`), behind a `NativeSharedPtr<NativeHeightmapData>` component. `NativeHeightmapFollower` is a `[WrapAsJob]` Burst job that resolves the pointer via `NativeWorldAccessor.SharedPtrResolver` and reads the heights inline. Simplest seed path: the registered descriptor builder returns the struct by value, and on a cache miss it's memcpy'd into a fresh heap slot. Capped at 256 cells by the inline storage.

- **`NativeSharedPtrTakingOwnership`** — `NativeHeightmapDataLarge` is a small header struct (descriptor) with a `BlobArray<float> Heights` field whose data lives in the same allocation via relative offsets. The registered builder returns a [`BlobBuilder`](../experimental/blob-builder.md)-laid-out `NativeBlobAllocation` the heap takes ownership of: it reserves space for the root and heights together, fills the heights into its working buffer, then hands over a single persistent allocation. Saves the stack-to-field copy the inline flavor pays, and has no inline cap; same Burst-job read path as the inline variant.

- **`ManagedSharedPtrInterface`** — `MutableHeightmapData` is a mutable managed class (public fields, populated via an object initializer rather than a constructor), exposed to entity-side callers through the `[Immutable]` `IReadOnlyHeightmapData` interface. The `SharedPtr<IReadOnlyHeightmapData>` handle is the same 12-byte shape as `ManagedSharedPtr`; only the type parameter differs. `InterfaceHeightmapFollower` reads through the interface on the main thread — the underlying concrete's mutable surface is unreachable from there without an explicit downcast. See [Shared Heap Data](../experimental/shared-heap-data.md) for when to pick the class route vs the interface route.

In all four flavors, every character entity holds its own small handle (12 bytes for the managed `SharedPtr` flavors, 4 bytes for the native `NativeSharedPtr` flavors) but all handles point at the same underlying blob — the heightmap data lives once on the heap regardless of character count.

### Which native flavor to reach for?

| | `NativeSharedPtrInline` | `NativeSharedPtrTakingOwnership` |
|---|---|---|
| **Size cap** | ≤ 256 cells (`FixedArray256<float>`) | unbounded |
| **Seed copies** | one stack-to-field copy into the local blob, then one memcpy into the heap | one memcpy from builder chunks into the heap (the stack-to-field hop is gone) |
| **Seed-site code** | builder returns the struct: `d => BuildNativeHeightsInline(d)` | builder returns a `NativeBlobAllocation`: `BlobBuilder` → `Allocate(in root.Heights, cells)` → `BuildNativeBlobAllocation()` |
| **Read site** | identical (Burst job over `NativeSharedPtr<T>`) | identical (Burst job over `NativeSharedPtr<T>`) |

Pick `Inline` when the blob comfortably fits in `FixedArray256<T>` and the simpler seed code wins. Pick `TakingOwnership` when the blob exceeds that cap, or when the one-time seed-side copy is genuinely on a hot path (rare — the seed runs once per unique descriptor, since the `BlobId` cache absorbs repeat builds).

## Why behind a pointer instead of inline?

`NativeHeightmapData` is fully self-contained — descriptor plus `FixedArray256<float>` — and only about 1 KB. You could put it directly on each character's component and skip the pointer entirely. So why the indirection?

1. **Deduplication.** Each character holds a small handle (4–12 bytes depending on pointer type), not its own copy of the heightmap. Inline would mean *N* characters × 1 KB of duplicated bytes that the chunk iterator strides over on every frame, even though every copy is identical.
2. **Chunk-row size.** ECS iteration cost scales with the row size of each archetype. A small handle keeps the character archetype lean; a 1 KB inline blob would make every system iterating these characters touch 1 KB per entity, even systems that never read the heightmap.
3. **Snapshot cost** — the next section.

If the blob were per-entity rather than shared (a different heightmap per character), reasons (1) and (3) would still favour `NativeSharedPtr` with a content-derived `BlobId`; reason (2) less so.

## Snapshot cost — why this scales

For rollback netcode, replay recording, or any system that snapshots state, the per-entity snapshot captures the `BlobId` — not the blob's contents. The blob bytes live on the world's heap, outside the per-frame snapshot path, because `NativeSharedPtr<T>` is immutable: there's nothing to capture frame-to-frame.

Snapshot cost per pointer is **constant regardless of payload size**. A 1 MB heightmap referenced by 1000 entities costs 1000 `BlobId`s in each snapshot — not 1000 × 1 MB. Inline storage of the same blob would scale rollback memory with `payload_size × entity_count × ring_buffer_depth`, which gets prohibitive once payloads grow.

On restore, the heap must already contain a blob under the captured `BlobId`. The seeder pattern handles this: a long-lived holder (here, `SceneInitializer`) keeps the blob alive across the rollback window so entity-side handles always resolve. See [Shared Heap Data](../experimental/shared-heap-data.md#seeding).

## The content-recipe pattern

The interesting bit is how the `BlobId` is derived. Define a struct capturing every input that determines the blob's content:

```csharp
public readonly partial struct HeightmapDescriptor
{
    public int Resolution { get; init; }
    public float WorldSize { get; init; }
    public float MaxHeight { get; init; }
    public uint Seed { get; init; }
    public float Frequency { get; init; }
}
```

Register a `BlitSerializer<HeightmapDescriptor>` so the descriptor can be hashed to a content-derived `BlobId`:

```csharp
var worldBuilder = new WorldBuilder()
    .RegisterSerializer(new BlitSerializer<HeightmapDescriptor>());
```

Then **register a builder once per descriptor type and acquire straight from a descriptor value**. `Acquire<TDesc, T>` hashes the descriptor, deduplicates against the cache, and runs the builder only on a miss — same recipe ⇒ same hash ⇒ same `BlobId` ⇒ same cached blob. The builder must rebuild the same data purely from its descriptor, because the cache may evict the blob and re-run it later:

```csharp
// Setup — one builder per descriptor type.
SharedAnchor.Register<HeightmapDescriptor, HeightmapData>(
    world,
    d => new HeightmapData(d, HeightmapBuilder.BuildManagedHeights(d)));

// Pin by descriptor through an anchor — builds on a miss, dedups on a hit.
_managedAnchor = SharedAnchor.Acquire<HeightmapDescriptor, HeightmapData>(world, descriptor);
```

The inline native flavor is the same shape — register a builder that returns the unmanaged struct by value, acquire by descriptor. On a miss the returned struct is copied into a fresh heap slot:

```csharp
NativeSharedAnchor.Register<HeightmapDescriptor, NativeHeightmapData>(
    world,
    d => HeightmapBuilder.BuildNativeHeightsInline(d));

_nativeAnchor = NativeSharedAnchor.Acquire<HeightmapDescriptor, NativeHeightmapData>(world, descriptor);
```

The taking-ownership flavor registers a builder that returns a `NativeBlobAllocation` — laid out by [`BlobBuilder`](../experimental/blob-builder.md) as a single contiguous allocation the heap takes ownership of — for blobs larger than the inline cap:

```csharp
NativeSharedAnchor.Register<HeightmapDescriptor, NativeHeightmapDataLarge>(
    world,
    BuildLargeHeightmapAllocation);

_nativeLargeAnchor =
    NativeSharedAnchor.Acquire<HeightmapDescriptor, NativeHeightmapDataLarge>(world, descriptor);

static NativeBlobAllocation BuildLargeHeightmapAllocation(HeightmapDescriptor descriptor)
{
    var cells = descriptor.Resolution * descriptor.Resolution;

    using var builder = new BlobBuilder(Allocator.Temp);
    ref var root = ref builder.ConstructRoot<NativeHeightmapDataLarge>();
    root.Descriptor = descriptor;

    var heights = builder.Allocate(in root.Heights, cells);
    // Fill the heights directly into the builder's reserved region.
    for (int z = 0; z < descriptor.Resolution; z++)
        for (int x = 0; x < descriptor.Resolution; x++)
            heights[z * descriptor.Resolution + x] =
                HeightmapBuilder.SampleNoise(x, z, descriptor);

    return builder.BuildNativeBlobAllocation();
}
```

The seeder holds these `SharedAnchor` / `NativeSharedAnchor` handles for the scene's lifetime to keep the blob resident; each character entity then acquires its own ECS-refcounted handle to the same blob by the anchor's id — `SharedPtr.Acquire<T>(world, anchor.BlobId)` (or the `NativeSharedPtr` equivalent).

Routing every flavor through a *registered descriptor builder* — rather than an eager one-shot allocation — is what lets the blob re-derive from its descriptor after an eviction, automatically and identically across all four flavors. See [BlobBuilder](../experimental/blob-builder.md) for the relative-offset layout that makes the large blob relocatable.

## Sampling in a Burst job

The native flavor's sampling system is a `[WrapAsJob]` static method that resolves the pointer through the `NativeWorldAccessor`:

```csharp
[ForEachEntity(typeof(SampleTags.Character), typeof(SampleTags.NativeFollower))]
[WrapAsJob]
static void Execute(
    in NativeHeightmapRef heightmap,
    ref Position position,
    in NativeWorldAccessor world)
{
    var data = heightmap.Value.Read(world).Value;
    // ... bilinear sample over data.Heights, write to position.Value.y
}
```

The source generator wraps the static method into a Burst-compiled job struct. Per-blob `AtomicSafetyHandle`s on `NativeSharedRead<T>` are read-only, so many jobs can read the same shared blob in parallel without contention.

## When to reach for this

- The blob is expensive to build but is a pure function of a small set of inputs — cave geometry, navmesh, level layouts, baked AI behaviour, mesh colliders, etc.
- You want different runs to share the cached result automatically when the inputs match, without any caller having to remember to choose a stable id by hand.
- The same blob may be requested by many independent subsystems and you want them to converge on one slot in the cache without coordination.
- You snapshot or rollback game state and don't want large immutable blobs duplicated into the rollback ring buffer.

For "I know up front this is `Warm` and that one is `Cool`", use hand-authored `BlobId` constants — [Sample 14](14-blob-seed-pattern.md). For per-entity mutable data, use `UniquePtr<T>` — [Sample 10 — Dynamic Collections](10-dynamic-collections.md).

## Cleanup discipline

Same as Sample 14: the scene initializer holds the seeder anchor (`SharedAnchor` / `NativeSharedAnchor`) as a member and disposes it explicitly. Entity-owned handles aren't disposed in this sample because no entities are removed during play; if you adapt the pattern to entities that come and go, register an `OnRemoved` observer to dispose each entity's handle as in [Sample 10](10-dynamic-collections.md).

## Concepts introduced

- **Content-derived `BlobId`** — `Acquire<TDesc, T>(world, descriptor)` hashes the descriptor to the id internally; you never pick it by hand
- **`Register<TDesc, T>` + `Acquire<TDesc, T>`** — register a per-descriptor builder once, then acquire by descriptor; the build runs only on a cache miss and re-runs identically after an eviction
- **`NativeSharedPtr` + `[WrapAsJob]`** — Burst-job sampling of an unmanaged shared blob via `NativeWorldAccessor`
- **`BlobBuilder` + `BlobArray<T>`** — relocatable single-allocation blob layout via relative offsets; the builder returns a `NativeBlobAllocation` with no `unsafe` code. See [BlobBuilder](../experimental/blob-builder.md).
- **`BlitSerializer<T>`** — registered so the descriptor can be hashed to its content-derived id
- **Off-snapshot storage** — immutable shared blobs sit outside the per-frame snapshot path; only the `BlobId` round-trips on snapshot / rollback
