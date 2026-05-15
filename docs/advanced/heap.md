# Heap

Components are unmanaged structs, so they can't hold classes, arrays, or other managed references directly. The **heap** system provides pointer types that let components reference data stored outside the component buffer.

This page covers pointer mechanics. For sharing patterns, stable identity, and which role can allocate which heap, see [Shared Heap Data](shared-heap-data.md).

## Pointer types

| Type | Ownership | Mutability | Data type | Burst-safe |
|------|-----------|------------|-----------|------------|
| `UniquePtr<T>` | Single owner | Mutable | Managed (`class`) | No |
| `SharedPtr<T>` | Reference counted | Immutable | Managed (`class`) | No |
| `NativeUniquePtr<T>` | Single owner | Mutable | Unmanaged (`struct`) | Yes |
| `NativeSharedPtr<T>` | Reference counted | Immutable | Unmanaged (`struct`) | Yes |

Allocation and access are static factories / instance methods on each pointer type — they take a `WorldAccessor` (or its `Heap` accessor). There are no `world.Heap.Alloc*` shortcuts.

## Allocating

```csharp
// Managed payloads
UniquePtr<MyData> unique = UniquePtr.Alloc(World, new MyData());
SharedPtr<MyData> shared = SharedPtr.Alloc(World, MyBlobs.Foo, new MyData());

// Unmanaged payloads (Burst-safe)
NativeUniquePtr<NativeData> nativeUnique = NativeUniquePtr.Alloc(World, new NativeData());
NativeSharedPtr<NativeData> nativeShared = NativeSharedPtr.Alloc(World, MyBlobs.Bar, new NativeData());
```

`SharedPtr` / `NativeSharedPtr` require a caller-supplied `BlobId` — shared blobs are addressed by stable ID so multiple call sites can resolve to the same allocation and so snapshots can round-trip the reference. See [Shared Heap Data](shared-heap-data.md) for the seeder / lookup patterns.

`UniquePtr` / `NativeUniquePtr` are single-owner so no ID is needed — the handle itself is the only reference.

Every `Alloc(world, …)` overload also has a `(world.Heap, …)` form for callers that already hold a `HeapAccessor`.

## Reading and writing

Managed pointers expose `Get(...)` directly; native pointers go through typed `Read(...)` / `Write(...)` wrappers so Unity's job-safety system can track conflicts:

```csharp
// Managed — Get returns the object reference
MyData data = unique.Get(World);
MyData shared_data = shared.Get(World);

if (shared.TryGet(World, out MyData maybe)) { /* … */ }

// Native — Read / Write hand back a safety-checked wrapper exposing .Value
ref readonly NativeData rd = ref nativeUnique.Read(World).Value;
ref NativeData wd = ref nativeUnique.Write(World).Value;
wd.HitCount++;

// NativeSharedPtr is read-only (immutable shared data) — no Write wrapper.
ref readonly NativeData sd = ref nativeShared.Read(World).Value;
```

To swap out the payload of a managed `UniquePtr` wholesale (replace the referenced object), call `Set(World, newValue)`.

## Storing pointers in components

Pointer structs are unmanaged, so they live directly in component fields:

```csharp
public struct CMeshReference : IEntityComponent
{
    public SharedPtr<Mesh> Mesh;
}

// At entity creation
World.AddEntity<MyTag>()
    .Set(new CMeshReference { Mesh = SharedPtr.Alloc(World, MeshIds.Bullet, mesh) });

// At a system call site
ref readonly CMeshReference meshRef = ref World.Component<CMeshReference>(entity).Read;
Mesh mesh = meshRef.Mesh.Get(World);
```

## Wrapping native collections

Native collection types (`NativeList<T>`, `NativeHashMap<K,V>`, `NativeQueue<T>`, etc.) hold an internal pointer to externally-allocated storage, so they can't sit directly inside a component. Trecs serializes a component as raw memory and expects it to be self-contained — a bare `NativeList` field would write its pointer bytes, not the elements behind them, and snapshots/recordings would silently drop the contents. Wrap them in a `NativeUniquePtr` to put the collection on Trecs's heap, where serialization knows to walk the inner data:

```csharp
public partial struct CCollisionPairBuffer : IEntityComponent
{
    public NativeUniquePtr<NativeList<CollisionPair>> Value;
}
```

For collections with known bounds, [`FixedList<N>`](fixed-collections.md) is usually simpler — it stores data inline in the component and needs no manual disposal.

## Shared pointers and reference counting

`SharedPtr<T>` and `NativeSharedPtr<T>` use reference counting. `Clone` returns a new handle to the same blob and bumps the refcount:

```csharp
SharedPtr<MyData> first  = SharedPtr.Alloc(World, MyBlobs.Foo, new MyData());
SharedPtr<MyData> second = first.Clone(World);  // same blob; refcount = 2
```

Calling `SharedPtr.Alloc(World, sameId)` (the lookup-only overload — no value argument) is equivalent to `Clone` addressed by ID instead of by reference: it finds the existing blob, bumps the refcount, and returns a fresh handle. See [Shared Heap Data — Pattern B](shared-heap-data.md#pattern-b-look-up-by-stable-blobid).

## Disposing

Pointers must be manually disposed when no longer needed:

```csharp
unique.Dispose(World);
shared.Dispose(World);   // decrements ref count; frees when it hits zero
```

### Cleanup is manual for entity-owned pointers

Pointers stored on components must be disposed when the entity is removed — Trecs does **not** auto-dispose. The standard pattern is an `OnRemoved` observer with a `[ForEachEntity]` handler that receives the component(s) to dispose:

```csharp
public partial class TrailCleanup : IDisposable
{
    readonly DisposeCollection _disposables = new();

    public TrailCleanup(World world)
    {
        World = world.CreateAccessor(AccessorRole.Fixed);

        World.Events
            .EntitiesWithTags<PatrolTags.Follower>()
            .OnRemoved(OnFollowerRemoved)
            .AddTo(_disposables);
    }

    WorldAccessor World { get; }

    [ForEachEntity]
    void OnFollowerRemoved(in Trail trail)
    {
        trail.Value.Dispose(World);
    }

    public void Dispose() => _disposables.Dispose();
}
```

See [Sample 10 — Pointers](../samples/10-pointers.md) for a runnable example and [Entity Events](../entity-management/entity-events.md) for the full observer API.

!!! warning
    Forgetting to dispose pointers causes memory leaks. Trecs reports leaks at world shutdown in debug builds.

## Pointers in jobs

Only the **native** variants (`NativeUniquePtr<T>` / `NativeSharedPtr<T>`) work inside Burst jobs. Inside a job, resolve through the matching resolver wired in via the `NativeWorldAccessor`:

```csharp
[ForEachEntity(typeof(MyTag))]
[WrapAsJob]
static void Execute(ref Trail trail, in NativeWorldAccessor world)
{
    ref var trailData = ref trail.Value.Write(world.UniquePtrResolver).Value;
    // ... mutate trailData ...
}
```

`Write` on a `ref` component requires a writable reference to the pointer struct itself — see the [Gotchas entry](../guides/gotchas.md#mutating-a-nativeuniqueptrt-needs-write-access-to-the-owning-component).

## See also

- [Sample 10 — Pointers](../samples/10-pointers.md): managed `UniquePtr` per entity.
- [Sample 13 — Native Pointers](../samples/13-native-pointers.md): the Burst-compatible equivalent inside a `[WrapAsJob]` job.
- [Sample 15 — Blob Storage](../samples/15-blob-storage.md): the seeder pattern with stable `BlobId` for shared assets.
