# Pointers

!!! warning "Experimental"
    The pointer types on this page are experimental — including `SharedPtr`, `UniquePtr`, their `Native*` and `Input*` siblings, and the surrounding heap allocation API. Shapes and names may change in future 0.x releases.

Components are unmanaged structs, so they can't hold classes, arrays, or other managed references directly. The **heap** is the storage Trecs provides for data that needs to outlive a single component value or be reached by reference — managed objects, growable native collections, large shared blobs. **Pointers** are the small handle structs you put on a component to refer to a heap allocation.

This page covers the full pointer surface — both the persistent pointers (entity-owned, alive until you dispose them) and the input pointers (frame-scoped, used inside `[Input]` components). For sharing patterns and seeders, see [Shared Heap Data](shared-heap-data.md).

## Persistent pointer types

| Type | Ownership | Mutability | Data type | Burst-safe |
|------|-----------|------------|-----------|------------|
| `UniquePtr<T>` | Single owner | Mutable | Managed (`class`) | No |
| `SharedPtr<T>` | Reference counted | Immutable | Managed (`class`) | No |
| `NativeUniquePtr<T>` | Single owner | Mutable | Unmanaged (`struct`) | Yes |
| `NativeSharedPtr<T>` | Reference counted | Immutable | Unmanaged (`struct`) | Yes |

### Allocating

```csharp
// Managed payloads
UniquePtr<MyData> unique = UniquePtr.Alloc(World, new MyData());
SharedPtr<MyData> shared = SharedPtr.Alloc(World, MyBlobs.Foo, new MyData());

// Unmanaged payloads (Burst-safe)
NativeUniquePtr<NativeData> nativeUnique = NativeUniquePtr.Alloc<NativeData>(World);
NativeSharedPtr<NativeData> nativeShared = NativeSharedPtr.Alloc(World, MyBlobs.Bar, new NativeData());
```

`SharedPtr` / `NativeSharedPtr` require a caller-supplied `BlobId` — shared blobs are addressed by stable ID so multiple call sites can resolve to the same allocation and so snapshots can round-trip the reference. See [Shared Heap Data](shared-heap-data.md) for the seeder / lookup patterns.

`UniquePtr` / `NativeUniquePtr` are single-owner so no ID is needed — the handle itself is the only reference.

### Reading and writing

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

`NativeSharedRead<T>.Value` returns `ref readonly T`. The canonical local-read pattern is:

```csharp
ref readonly var collider = ref colliderPtr.Read(World).Value;
DoSomething(collider.MassProperties);

// Or, when the getter is used exactly once, fold it inline:
DoSomething(colliderPtr.Read(World).Value.MassProperties);
```

To swap out the payload of a managed `UniquePtr` wholesale (replace the referenced object), call `Set(World, newValue)`.

### Storing pointers in components

Pointer structs are unmanaged, so they live directly in component fields:

```csharp
public partial struct CMeshReference : IEntityComponent
{
    public SharedPtr<Mesh> Mesh;
}

// At entity creation
World.AddEntity<MyTag>()
    .Set(new CMeshReference { Mesh = SharedPtr.Alloc(World, MeshIds.Bullet, mesh) });

// At a system call site
ref readonly CMeshReference meshRef = ref entity.Component<CMeshReference>(World).Read;
Mesh mesh = meshRef.Mesh.Get(World);
```

### Storing native collections

Native collection types (`NativeList<T>`, `NativeHashMap<K,V>`, etc.) can't sit directly in a component — see [Dynamic Collections](dynamic-collections.md) for why, and for the preferred alternatives (`FixedList<N>`, `TrecsList<T>`).

When you specifically need a Unity `NativeList<T>` (e.g. to share with non-Trecs code), wrap it in a `NativeUniquePtr<NativeList<T>>`.

### Shared pointers and reference counting

`SharedPtr<T>` and `NativeSharedPtr<T>` use reference counting. `Clone` returns a new handle to the same blob and bumps the refcount:

```csharp
SharedPtr<MyData> first  = SharedPtr.Alloc(World, MyBlobs.Foo, new MyData());
SharedPtr<MyData> second = first.Clone(World);  // same blob; refcount = 2
```

Calling `SharedPtr.Acquire(World, sameId)` is equivalent to `Clone` addressed by ID instead of by reference: it finds the existing blob, bumps the refcount, and returns a fresh handle. See [Shared Heap Data — Pattern B](shared-heap-data.md#pattern-b-look-up-by-stable-blobid).

### Disposing

Pointers must be manually disposed when no longer needed:

```csharp
unique.Dispose(World);
shared.Dispose(World);   // decrements ref count; frees when it hits zero
```

#### Cleanup is manual for entity-owned pointers

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

### Pointers in jobs

Only the **native** variants (`NativeUniquePtr<T>` / `NativeSharedPtr<T>`) work inside Burst jobs. Inside a job, resolve through the matching resolver wired in via the `NativeWorldAccessor`:

```csharp
[ForEachEntity(typeof(MyTag))]
[WrapAsJob]
static void Execute(ref Trail trail, in NativeWorldAccessor world)
{
    ref var trailData = ref trail.Value.Write(world.ChunkStoreResolver).Value;
    // ... mutate trailData ...
}
```

`Write` on a `ref` component requires a writable reference to the pointer struct itself — see the [Gotchas entry](../guides/gotchas.md#mutating-a-nativeuniqueptrt-needs-write-access-to-the-owning-component).

## Input pointer types

Plain `[Input]` components hold fixed-size data. When the value queued onto an `[Input]` field is variable-sized or large enough that copying it into a component is wasteful — a network packet, a serialized command list, a managed object reference — use one of the **input pointer** types instead of a persistent pointer. They share the same `Read` / `Write` shape as their persistent siblings but their backing storage is **bulk-released when the target input frame retires**, so they fit the per-frame nature of [`[Input]` fields](../core/input-system.md).

| Type | Backing storage | When to use |
|------|-----------------|-------------|
| `InputNativeUniquePtr<T>` (where `T : unmanaged`) | Per-allocation native buffer (Burst-readable) | Owned, unmanaged payloads — small variable-sized buffers, one writer per allocation. |
| `InputNativeSharedPtr<T>` (where `T : unmanaged`) | Refcounted blob handle | Shared unmanaged data that can be referenced from multiple input components at once. |
| `InputSharedPtr<T>` (where `T : class`) | Refcounted blob handle (managed) | Shared managed objects (read-only views, big readonly data). |
| `InputUniquePtr<T>` (where `T : class`) | Pool-managed instance | Owned managed objects pulled from an `ITrecsPoolManager`. |

### Allocating

```csharp
public partial struct NetworkPacket : IEntityComponent
{
    public InputNativeUniquePtr<NetworkPacketBody> Body;
}

[ExecuteIn(SystemPhase.Input)]
public partial class IngestPacketsSystem : ISystem
{
    public void Execute(EntityHandle handle)
    {
        var body = new NetworkPacketBody { /* ... */ };
        var ptr = InputNativeUniquePtr.Alloc(World, body);
        handle.AddInput(World, new NetworkPacket { Body = ptr });
    }
}
```

`Alloc` is the only factory — input-pointer types have **no `Dispose`** because the backing storage is bulk-released when the target input frame retires. Reading is the same shape as the persistent siblings (`ptr.Read(world)` or `ptr.Read(in resolver)` from a job).

### Source-generator rules

A few rules the source generator enforces at compile time on `[Input]` template fields:

- **No persistent pointer types** (`NativeUniquePtr`, `NativeSharedPtr`, `SharedPtr`, `UniquePtr`) inside `[Input]` components — TRECS121. Use the `Input{Name}Ptr` variant instead, otherwise allocations from input systems leak when the frame is retired.
- **No `TrecsList<T>`** inside `[Input]` components — TRECS122. `TrecsList` is backed by the persistent chunk store and has no input-side equivalent today; use a fixed-size buffer for small bounded lists.
- **`MissingInputBehavior.Retain` is incompatible with any `InputXxxPtr` field** on the same component — TRECS123. Retain would keep the previous frame's handle, which now points at storage that was freed when the previous input frame retired. Use `MissingInputBehavior.Reset` (which zeros the handle each frame an input doesn't arrive) or move the pointed-to data out of the input component.

## See also

- [Shared Heap Data](shared-heap-data.md) — seeder patterns and `BlobId` strategies for shared blobs.
- [Input System](../core/input-system.md) — the surrounding model for `[Input]` components, `AddInput`, and recording / replay.
- [Sample 10 — Pointers](../samples/10-pointers.md) — managed `UniquePtr` per entity.
- [Sample 14 — Blob Seed Pattern](../samples/14-blob-seed-pattern.md) — the seeder pattern with stable `BlobId` for shared assets.
