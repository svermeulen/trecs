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

## Allocating

`Heap` is a property on `WorldAccessor` (the source-generated `World` property inside an `ISystem` works directly):

```csharp
UniquePtr<MyData> unique = World.Heap.AllocUnique(new MyData());
SharedPtr<MyData> shared = World.Heap.AllocShared(new MyData());

NativeUniquePtr<NativeData> nativeUnique = World.Heap.AllocNativeUnique(new NativeData());
NativeSharedPtr<NativeData> nativeShared = World.Heap.AllocNativeShared(new NativeData());
```

For shared allocations that need a stable identity across runs (so clones from disk resolve to the same heap entry), use the `BlobId` overloads — see [Pattern B — look up by stable `BlobId`](shared-heap-data.md#pattern-b--look-up-by-stable-blobid).

## Reading

`Get` / `TryGet` / `Dispose` / `Clone` accept either a `HeapAccessor` or a `WorldAccessor` directly:

```csharp
// Managed
MyData data = unique.Get(World);
MyData data2 = shared.Get(World);

// Native (return refs)
ref readonly NativeData d1 = ref nativeUnique.Get(World);
ref readonly NativeData d2 = ref nativeShared.Get(World);

// Safe access
if (shared.TryGet(World, out MyData data3)) { ... }
```

## Storing pointers in components

Pointer structs are unmanaged, so they can be stored as component fields:

```csharp
public struct CMeshReference : IEntityComponent
{
    public SharedPtr<Mesh> Mesh;
}

// Set during entity creation
World.AddEntity<MyTag>()
    .Set(new CMeshReference { Mesh = World.Heap.AllocShared(mesh) });

// Read in a system
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

For collections with known bounds, [`FixedList<N>`](fixed-collections.md) can be easier, since it stores data inline with component and therefore doesn't require manual disposal.

## Shared pointers and reference counting

`SharedPtr<T>` and `NativeSharedPtr<T>` use reference counting. Multiple components can reference the same data:

```csharp
SharedPtr<MyData> original = World.Heap.AllocShared(new MyData());
SharedPtr<MyData> clone = original.Clone(World);  // Increments ref count
```

## Disposing

Pointers must be manually disposed when no longer needed:

```csharp
unique.Dispose(World);
shared.Dispose(World);  // Decrements ref count; frees if zero
```

### Cleanup is manual for entity-owned pointers

Pointers stored on components must be disposed when the entity is removed — Trecs does **not** auto-dispose. The standard pattern is an `OnRemoved` observer on the relevant tag:

```csharp
accessor.Events.EntitiesWithTags<MyTag>()
    .OnRemoved((group, indices, world) =>
    {
        var refs = world.ComponentBuffer<PaletteRef>(group).Read;
        for (int i = indices.Start; i < indices.End; i++)
        {
            refs[i].Value.Dispose(world.Heap);
        }
    });
```

See [Sample 10 — Pointers](../samples/10-pointers.md) for a reference implementation.

!!! warning
    Forgetting to dispose pointers causes memory leaks. Trecs detects leaks at world shutdown in debug builds.

## Pointers in jobs

Use native pointer types (`NativeUniquePtr<T>` / `NativeSharedPtr<T>`) in jobs and call `Get` with a `NativeWorldAccessor`:

```csharp
ref readonly NativeData data = ref nativeShared.Get(in nativeWorld);
```

## See also

- [Sample 10 — Pointers](../samples/10-pointers.md): unique and shared managed pointers stored on entities.
- [Sample 17 — Blob Storage](../samples/17-blob-storage.md): the seeder pattern with stable `BlobId` for shared assets.
