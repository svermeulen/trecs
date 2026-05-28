# BlobBuilder

!!! warning "Experimental"
    `BlobBuilder`, `BlobArray<T>`, and the surrounding `NativeSharedPtr.AllocTakingOwnership` flow are experimental and may change in future 0.x releases.

`BlobBuilder` builds a *relocatable blob*: a single contiguous native allocation whose internal references are stored as **offsets relative to each offset field's own address**, not relative to the root or as absolute pointers. That single design choice gives the blob three properties for free:

1. **Memcpy-relocatable.** Copy the entire allocation to a new address and every internal reference still resolves — each offset is a delta from its own location, which moves with the rest of the blob.
2. **Type-honest.** The relative-pointer field types (`BlobArray<T>`) declare exactly the storage they use (8 bytes for an array — offset + length). No "the type lies about its actual size" trick where `sizeof(T)` understates the real allocation.
3. **No call-site `unsafe` for typical flows.** Authoring a blob with indexer-based fills is a pure-managed `using` block; the unsafe layout work lives inside `BlobBuilder`. Bulk fills via `BlobBuilderArray<T>.GetUnsafePtr()` still require `unsafe`, but most call sites don't need that.

The output is a `NativeBlobAllocation` ready for `NativeSharedPtr.AllocTakingOwnership` — same component-storage and snapshot story as any other native shared blob. See [Shared Heap Data](shared-heap-data.md) for the surrounding seeder / `BlobId` patterns.

## When to reach for it

When a native shared blob contains variable-length data and the inline-storage options (`FixedArray256<T>` and friends) don't fit:

- The size depends on per-blob inputs (heightmap resolution, navmesh polycount, baked AI behaviour table size).
- The size can exceed inline-storage caps (`FixedArray256<T>` is the largest inline-array).
- You want a single allocation per blob — not an outer `NativeSharedPtr<T>` plus a separate `TrecsArray<T>` for the payload with two lifecycles to manage.

If your blob fits inline, prefer the simpler `NativeSharedPtr.Alloc(in value)` flow — no builder, just declare a fixed-size struct and let the heap memcpy the value in.

## The pieces

- **`BlobArray<T>`** — 8-byte readonly struct: `int m_OffsetPtr` (delta from `&m_OffsetPtr` to the first element) + `int m_Length`. Lives as a field inside the blob root. Read-only at runtime; the indexer returns by `ref readonly T` so reads stay zero-copy.
- **`BlobBuilder`** — disposable `ref struct`. Owns a chunked working buffer during construction; reserves space for the root and for each `BlobArray<T>` field; tracks the offset-patches needed to wire them up; finalizes everything into one fresh `Allocator.Persistent` allocation at `Build` time.
- **`BlobBuilderArray<T>`** — write-side view returned by `Allocate`. Indexer returns by writable `ref T`. Valid until the builder is disposed.

## A complete example

A blob describing a level: a fixed-size header plus a variable-length array of room descriptors.

```csharp
public partial struct Level
{
    public int LevelIndex;
    public float SizeMeters;
    public BlobArray<Room> Rooms;
}

public struct Room
{
    public float3 Center;
    public float Radius;
}
```

The blob root is a **plain `struct`** — not `readonly struct` — because TRECS124's defensive-copy-safety check passes as long as the struct has no non-readonly instance methods (a struct with no methods at all satisfies the rule vacuously). The seed site can then assign fields directly instead of going through a constructor or `Unsafe.AsRef` tricks. Post-seed mutation through the public API is still impossible because `NativeSharedRead<T>.Value` returns `ref readonly T`.

Seed it:

```csharp
NativeSharedPtr<Level> anchor;
using (var builder = new BlobBuilder(Allocator.Temp))
{
    ref var root = ref builder.ConstructRoot<Level>();
    root.LevelIndex = 4;
    root.SizeMeters = 128f;

    var rooms = builder.Allocate(in root.Rooms, descriptors.Length);
    for (int i = 0; i < descriptors.Length; i++)
    {
        rooms[i] = new Room { Center = descriptors[i].Center, Radius = descriptors[i].Radius };
    }

    anchor = builder.Build<Level>(world, blobId);
}
```

If you'd rather use a `readonly struct` for the extra type-system "this can't be mutated" signal, that also works — TRECS124 accepts both shapes. With `readonly struct` you'd write `root = new Level(4, 128f);` (wholesale assignment via a constructor) instead of direct field assignment.

Read it (main thread or Burst job, no difference):

```csharp
ref readonly var level = ref anchor.Read(world).Value;
int idx = level.LevelIndex;
float r = level.Rooms[0].Radius;  // BlobArray<T>'s indexer
```

The component-side story is unchanged from any other `NativeSharedPtr<T>`:

```csharp
[Unwrap]
public partial struct LevelRef : IEntityComponent
{
    public NativeSharedPtr<Level> Value;
}
```

`BlobArray<T>` itself **cannot** be stored on a component — its offset field is meaningful only relative to its own address inside the enclosing blob. Always go through `NativeSharedPtr<T>` to the blob root, then index into `BlobArray<T>` fields from there.

## Construction order

Inside the `using` block:

1. **`ConstructRoot<T>()` first.** Reserves space for the root and returns a writable `ref T`. Call exactly once.
2. **Initialize the root's non-array fields.** Either assign them directly (as in the example above) or use wholesale assignment via a constructor — the latter works naturally with `readonly struct` root types.
3. **`Allocate(in root.SomeArray, length)` for each `BlobArray` field.** Reserves the array's storage and records a patch. Returns a `BlobBuilderArray<T>` for filling.
4. **Fill each array.** Either through the indexer (`arr[i] = ...`) or by writing directly through `arr.GetUnsafePtr()` for bulk fills.
5. **`Build<T>(world, blobId)`** to finalize and seed. The builder's working chunks are then disposed automatically by `using`.

You can interleave `Allocate` calls and array fills freely. The builder's chunked allocator keeps each `BlobBuilderArray<T>`'s data pointer stable across subsequent `Allocate` calls, so a partial fill of one array is still valid after allocating another.

## Alignment

`Allocate(in field, length)` uses `UnsafeUtility.AlignOf<T>()`. For most types that's what you want. Pass an explicit alignment via the third-argument overload (`Allocate(in field, length, alignment)`) when:

- The element type is a wrapper around a SIMD primitive (`Unity.Mathematics.float4` only reports `AlignOf` of 4 because alignment is derived from contained primitives, not the struct as a whole).
- You're laying out a structure that has a specific memory-bus alignment expectation.

Maximum alignment is 16 — the builder caps it so chunks (always 16-aligned) suffice to hold anything you allocate.

## Lifecycle and ownership

`BlobBuilder` and the blob it produces have **independent lifecycles**:

- The builder's working chunks are allocated in the allocator you pass to the constructor (typically `Allocator.Temp`) and freed by `Dispose`.
- `Build` allocates a separate `Allocator.Persistent` buffer, copies the working chunks into it with offsets patched, and hands it to `NativeSharedPtr.AllocTakingOwnership`. The heap now owns the buffer and frees it when the refcount hits zero.

The standard `using` pattern reflects this cleanly: the builder's working state cleans up at end-of-scope; the produced blob lives independently behind the `NativeSharedPtr<T>` you got back.

If you build but never hand off (e.g. an exception fires between `Build` and the next line), the buffer leaks — `Build` has already transferred it out of the builder by then. In practice the easiest way to make this airtight is to keep `Build` as the last statement before `using` exits.

## Relocatability in practice

The offsets-from-self design means a blob's bytes are *self-contained* and *relocatable*. The lower-level `BuildNativeBlobAllocation()` finalizer (instead of `Build<T>`) hands you the raw `(ptr, size, alignment)` triple, which makes the relocatability concrete:

```csharp
// Build a blob into a NativeBlobAllocation (the same call Build<T> wraps).
NativeBlobAllocation original;
using (var builder = new BlobBuilder(Allocator.Temp))
{
    ref var root = ref builder.ConstructRoot<Level>();
    // ... fill root + Allocate(in root.Rooms, ...) ...
    original = builder.BuildNativeBlobAllocation();
}

// Memcpy the raw bytes to a different address — the copy is fully valid.
byte* copy = (byte*)AllocatorManager.Allocate(
    Allocator.Persistent, original.AllocSize, original.Alignment, items: 1);
UnsafeUtility.MemCpy(copy, (void*)original.Ptr, original.AllocSize);

// `copy` is now a fully-functional Level blob at a different address.
// Every BlobArray inside still resolves correctly because each offset is a
// delta from its own location, not from any absolute pointer.
ref readonly var moved = ref UnsafeUtility.AsRef<Level>(copy);
float r = moved.Rooms[0].Radius;  // works

// Both buffers need explicit freeing — neither went through the heap.
AllocatorManager.Free(Allocator.Persistent, copy, original.AllocSize, original.Alignment, items: 1);
AllocatorManager.Free(Allocator.Persistent, (void*)original.Ptr, original.AllocSize, original.Alignment, items: 1);
```

In real code you'd hand `original` to `NativeSharedPtr.AllocTakingOwnership` (which is what `Build<T>` does for you) and let the heap manage freeing. This snippet just shows that the bytes themselves are self-contained.

This same property is what lets a blob round-trip through serialization or asset bundles without pointer fixup — write the bytes, load them back into any address, dereference normally.

## Comparison with other approaches

| Pattern | When to use | Notes |
|---|---|---|
| `NativeSharedPtr.Alloc(in T value)` | T is a fixed-size struct (with or without `FixedArray256<T>` etc. inline). | Simplest. One memcpy of T into the heap. Capped at whatever inline storage T provides. |
| `BlobBuilder` + `NativeSharedPtr.AllocTakingOwnership` | T contains variable-length data that doesn't fit inline. | This page. Single allocation, type-honest, relocatable. |
| Separate `TrecsArray<float>` field on the component | Per-entity variable-length data (not shared between entities). | Outside the shared-blob story entirely. See [Dynamic Collections](dynamic-collections.md). |

## `BlobRef<T>` — single-T relative pointer

`BlobRef<T>` is the single-element counterpart to `BlobArray<T>`: a 4-byte field that stores a relative offset to one nested `T` rather than to an array. Same relocatable-by-construction property. Useful for:

- **Polymorphic blob shapes** — a root with a `BlobRef<Variant>` whose referent is one of several concrete types (discriminated by a header field).
- **Optional sub-structures** — `BlobRef<T>.IsValid` distinguishes "not allocated" from "allocated"; leave the field at `default` to mark it absent and skip the `Allocate` call.
- **In-blob cross-references** — multiple `BlobRef<T>` fields can target the same payload, or different parts of the blob can hold pointers into a shared region.

The DOTS equivalent is `BlobPtr<T>`, but that name is already taken in Trecs for a heap-pin type, so this ships as `Trecs.BlobRef<T>`.

```csharp
public struct PathSegment
{
    public float3 Start;
    public float3 End;
    public BlobRef<SegmentMeta> Meta;  // optional
}

NativeSharedPtr<PathSegment> anchor;
using (var builder = new BlobBuilder(Allocator.Temp))
{
    ref var seg = ref builder.ConstructRoot<PathSegment>();
    seg.Start = new float3(0, 0, 0);
    seg.End = new float3(10, 0, 0);

    // Allocate the optional payload, then fill it.
    ref var meta = ref builder.Allocate(in seg.Meta);
    meta = new SegmentMeta(...);

    anchor = builder.Build<PathSegment>(world, blobId);
}

// Read side:
ref readonly var path = ref anchor.Read(world).Value;
if (path.Meta.IsValid)
{
    ref readonly var meta = ref path.Meta.Value;
    // ...
}
```

`BlobRef<T>` is `[NonCopyable]` for the same reason as `BlobArray<T>` — a by-value copy onto the stack would mis-resolve the relative offset.

## Nested `BlobArray<T>`

A `BlobArray<T>` field inside an element of another `BlobArray<T>` works without any special API: the builder's indexer returns `ref TElement`, and the nested field's address lives in builder-owned chunk memory just like a root-level field. The pattern is:

```csharp
public struct Polygon { public int Id; public BlobArray<int> Vertices; }
public struct Region  { public int RegionId; public BlobArray<Polygon> Polygons; }
public struct NavMesh { public BlobArray<Region> Regions; }

using (var builder = new BlobBuilder(Allocator.Temp))
{
    ref var nav = ref builder.ConstructRoot<NavMesh>();

    var regions = builder.Allocate(in nav.Regions, regionCount);
    for (int i = 0; i < regionCount; i++)
    {
        regions[i] = new Region { RegionId = i, Polygons = default };

        // Nested Allocate — same call as a root-level one.
        var polys = builder.Allocate(in regions[i].Polygons, polyCount[i]);
        for (int j = 0; j < polyCount[i]; j++)
        {
            polys[j] = new Polygon { Id = j, Vertices = default };
            var verts = builder.Allocate(in polys[j].Vertices, vertexCount[i][j]);
            // fill verts...
        }
    }
    // build...
}
```

The `[NonCopyable]` rule propagates transitively, so `Polygon` (which has a `BlobArray<int>` field) and `Region` (which has a `BlobArray<Polygon>` field) both need to be marked `[NonCopyable]`.

## What's not (yet) supported

- **`BlobString`** (UTF-8 inline strings). Same shape as `BlobArray<T>` — offset + length — but not yet wired. File an issue if you have a use case.

If you hit one of the missing capabilities, file an issue.
