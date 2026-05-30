# Pointers

!!! warning "Experimental"
    The pointer types on this page are experimental — including `SharedPtr`, `UniquePtr`, their `Native*` and `Input*` siblings, and the surrounding heap allocation API. Shapes and names may change in future 0.x releases.

## What pointers are for

A Trecs component is an unmanaged struct, so it can hold numbers, `float3`s, and other fixed-size value data — but **not** a `class`, an array, a `string`, or a Unity object like a `Mesh`. So how does an entity "own" a mesh, an AI blackboard object, or a growable buffer?

Through the **heap** — a separate store Trecs manages for data that lives outside the component buffer. You allocate the object on the heap once and get back a **pointer**: a tiny handle struct (a few bytes) that *is* unmanaged, so it fits in a component. The component carries the lightweight handle; the handle resolves to the real object whenever you ask.

```csharp
UniquePtr<AiBrain> brain = UniquePtr.Alloc(World, new AiBrain());  // 1. put the object on the heap
enemy.Component<CEnemy>(World).Write.Brain = brain;                // 2. store the handle on a component
AiBrain b = brain.Get(World);                                      // 3. resolve it back when needed
brain.Dispose(World);                                              // 4. free it yourself — Trecs never auto-frees
```

The rest of this page is the detail behind those four steps. For cross-entity sharing and stable IDs, see [Shared Heap Data](shared-heap-data.md).

## Persistent pointer types

Two questions pick the type you need:

1. **Managed or unmanaged data?** A `class` (or anything holding managed references) is *managed*. A pure `struct` of blittable fields is *unmanaged* — only unmanaged data can be touched inside Burst jobs, and those types carry the `Native` prefix.
2. **One owner, or shared?** A **`Unique`** pointer has a single owner and is freely mutable. A **`Shared`** pointer is reference-counted — many handles to one allocation — and immutable, ideal for read-only data reused across many entities (meshes, lookup tables, blobs).

The two axes give four types:

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
NativeUniquePtr<NativeData> nativeUnique = NativeUniquePtr.Alloc(World, new NativeData());
NativeUniquePtr<NativeData> nativeUnique2 = NativeUniquePtr.Alloc<NativeData>(World); // uninitialized
NativeSharedPtr<NativeData> nativeShared = NativeSharedPtr.Alloc(World, MyBlobs.Bar, new NativeData());
```

`SharedPtr` / `NativeSharedPtr` require a caller-supplied `BlobId` so multiple call sites can resolve to the same allocation and snapshots can round-trip the reference. See [Shared Heap Data](shared-heap-data.md) for seeder and lookup patterns.

`UniquePtr` / `NativeUniquePtr` are single-owner — no ID needed.

For shared pointers, `GetOrAlloc` is a convenience that allocates on the first call and returns the existing blob on subsequent calls:

```csharp
SharedPtr<MyData> ptr = SharedPtr.GetOrAlloc(World, MyBlobs.Foo, () => new MyData());
NativeSharedPtr<NativeData> nPtr = NativeSharedPtr.GetOrAlloc(World, MyBlobs.Bar, () => new NativeData());
```

### Reading and writing

How you reach the payload depends on whether it's managed or native.

**Managed (`UniquePtr` / `SharedPtr`)** — the payload is a `class`, so `Get` simply hands you the object reference:

```csharp
MyData data = unique.Get(World);
MyData sharedData = shared.Get(World);

// Shared blobs can be freed elsewhere, so they also offer liveness checks:
if (shared.TryGet(World, out MyData maybe)) { /* resolved */ }
bool alive = shared.CanGet(World);   // check without resolving
```

**Native (`NativeUniquePtr` / `NativeSharedPtr`)** — the payload is a `struct` sitting in the heap buffer, so you don't want a copy. `Read` / `Write` hand back a small job-safety-checked wrapper, and the struct itself is its `.Value`, returned **by ref** — so you read or mutate it in place:

```csharp
ref readonly NativeData rd = ref nativeUnique.Read(World).Value;   // read-only view
ref NativeData wd = ref nativeUnique.Write(World).Value;           // writable view
wd.HitCount++;                                                     // writes straight to the heap data

// NativeSharedPtr is immutable shared data — Read only, no Write:
ref readonly NativeData sd = ref nativeShared.Read(World).Value;
```

Because `.Value` is a `ref`, you can skip the local and use it inline when you only need it once:

```csharp
DoSomething(colliderPtr.Read(World).Value.MassProperties);
```

Inside a Burst job, resolve through the `NativeWorldAccessor` instead of `World` — see [Pointers in jobs](#pointers-in-jobs).

**Replacing a managed object.** `Get` reads the current object; to repoint a `UniquePtr` at a *different* object, call `Set`. It's a `this ref` extension, so the call site needs a writable reference to the pointer struct — i.e. the owning component's `.Write`:

```csharp
ref var comp = ref entity.Component<CMyComp>(World).Write;
comp.Ptr.Set(World, newValue);
```

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

Native collection types (`NativeList<T>`, `NativeHashMap<K,V>`, etc.) can't sit directly in a component — see [Dynamic Collections](dynamic-collections.md) for the preferred alternatives (`FixedList<N>`, `TrecsList<T>`).

When you specifically need a Unity `NativeList<T>`, wrap it in a `NativeUniquePtr<NativeList<T>>`.

### Shared pointers and reference counting

`SharedPtr<T>` and `NativeSharedPtr<T>` use reference counting. `Clone` bumps the refcount and returns a handle to the same blob. Each clone must be independently disposed:

```csharp
SharedPtr<MyData> first  = SharedPtr.Alloc(World, MyBlobs.Foo, new MyData());
SharedPtr<MyData> second = first.Clone(World);  // same blob; refcount = 2
second.Dispose(World); // refcount = 1
first.Dispose(World);  // refcount = 0, blob freed
```

`SharedPtr.Acquire(World, blobId)` is `Clone` addressed by ID instead of by reference — it finds the existing blob, bumps the refcount, and returns a handle. See [Shared Heap Data — Pattern B](shared-heap-data.md#pattern-b-look-up-by-stable-blobid).

### Disposing { #cleanup-is-manual-for-entity-owned-pointers }

Pointers must be manually disposed — Trecs does **not** auto-dispose:

```csharp
unique.Dispose(World);
shared.Dispose(World);   // decrements ref count; frees when it hits zero
```

For pointers stored on components, use an `OnRemoved` observer to dispose when the entity is removed:

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

See [Sample 10 — Dynamic Collections](../samples/10-dynamic-collections.md) for a runnable example and [Entity Events](../entity-management/entity-events.md) for the full observer API.

!!! warning
    Forgetting to dispose pointers causes memory leaks. Trecs reports leaks at world shutdown in debug builds.

### Pointers in jobs

Only the **native** variants (`NativeUniquePtr<T>` / `NativeSharedPtr<T>`) work inside Burst jobs. Resolve through the `NativeWorldAccessor`:

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

When an `[Input]` component needs variable-sized or large data — a network packet, a serialized command list, a managed object — use an **input pointer** instead of a persistent pointer. Input pointers share the same `Read` / `Write` shape but their storage is **bulk-released when the input frame retires**, matching the per-frame nature of [`[Input]` fields](../core/input-system.md).

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

Input pointers have **no `Dispose`** — storage is bulk-released when the input frame retires. Reading works the same as persistent siblings (`ptr.Read(world)` or `ptr.Read(in resolver)` from a job).

### Source-generator rules

The source generator enforces these rules at compile time on `[Input]` components:

- **No persistent pointer types** inside `[Input]` components — TRECS121. Use the `Input*Ptr` variant instead; persistent allocations leak when the frame retires.
- **No `TrecsList<T>`** inside `[Input]` components — TRECS122. Use a fixed-size buffer instead.
- **`MissingInputBehavior.Retain` is incompatible with `Input*Ptr` fields** — TRECS123. Retain keeps the previous frame's handle, which points at freed storage. Use `MissingInputBehavior.Reset` instead.

## See also

- [Shared Heap Data](shared-heap-data.md) — seeder patterns and `BlobId` strategies for shared blobs.
- [Input System](../core/input-system.md) — the surrounding model for `[Input]` components, `AddInput`, and recording / replay.
- [Sample 10 — Dynamic Collections](../samples/10-dynamic-collections.md) — managed `UniquePtr` per entity.
- [Sample 14 — Blob Seed Pattern](../samples/14-blob-seed-pattern.md) — the seeder pattern with stable `BlobId` for shared assets.
