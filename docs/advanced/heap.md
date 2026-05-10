# Heap

Components are unmanaged structs, so they can't hold classes, arrays, or other managed references directly. The **heap** system provides pointer types that let components reference data stored outside the component buffer.

This page covers the pointer mechanics. For *which role can allocate which heap*, see [Heap Allocation Rules](heap-allocation-rules.md).

## Pointer types

| Type | Ownership | Mutability | Data type | Burst-safe |
|------|-----------|------------|-----------|------------|
| `UniquePtr<T>` | Single owner | Mutable | Managed (`class`) | No |
| `SharedPtr<T>` | Reference counted | Immutable | Managed (`class`) | No |
| `NativeUniquePtr<T>` | Single owner | Mutable | Unmanaged (`struct`) | Yes |
| `NativeSharedPtr<T>` | Reference counted | Immutable | Unmanaged (`struct`) | Yes |

## Allocating

`Heap` is a property on `WorldAccessor` (so the source-generated `World` property inside an `ISystem` works directly):

```csharp
UniquePtr<MyData> unique = World.Heap.AllocUnique(new MyData());
SharedPtr<MyData> shared = World.Heap.AllocShared(new MyData());

NativeUniquePtr<NativeData> nativeUnique = World.Heap.AllocNativeUnique(new NativeData());
NativeSharedPtr<NativeData> nativeShared = World.Heap.AllocNativeShared(new NativeData());
```

For shared allocations that need a stable identity across runs (so clones from disk resolve to the same heap entry), use the `BlobId` overloads — see [Stable BlobIds](heap-allocation-rules.md#stable-blobids-when-init-isnt-deterministic).

## Reading

`Get` / `TryGet` / `Dispose` / `Clone` accept either a `HeapAccessor` or a `WorldAccessor` directly:

```csharp
// Managed
MyData data = unique.Get(World);
MyData data2 = shared.Get(World);

// Native (return refs)
ref NativeData d1 = ref nativeUnique.Get(World);
ref NativeData d2 = ref nativeShared.Get(World);

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

Native collection types (`NativeList<T>`, `NativeHashMap<K,V>`, `NativeQueue<T>`, etc.) hold an internal pointer to externally-allocated storage, so they can't sit directly inside a component as-is. Wrapping them in a `NativeUniquePtr` is the standard pattern when you want one of these collections to be part of world state:

```csharp
public partial struct CCollisionPairBuffer : IEntityComponent
{
    public NativeUniquePtr<NativeList<CollisionPair>> Value;
}
```

The component now holds an unmanaged pointer (legal as a component field), the `NativeList` lives behind it, and Trecs's [serialization](serialization.md) walks the inner list's raw data — so snapshots and recordings capture the collection contents for free.

**Disposal caveat.** The inner collection's storage is allocated in Unity's allocator (whichever one you passed to `new NativeList<T>(allocator)`), not in Trecs's heap. Disposing the `NativeUniquePtr` only frees the heap slot holding the `NativeList` header — the underlying storage leaks unless you dispose the inner collection first:

```csharp
ref var list = ref buffer.Value.Get(world);
list.Dispose();              // free the NativeList's storage
buffer.Value.Dispose(world); // then free the heap slot
```

For entity-scoped buffers, do this in an `OnRemoved` handler. For world / scene globals (like `CCollisionPairBuffer`), do it in scene teardown before world disposal.

For fixed-size cases where serialization isn't a concern, [`FixedList<N>`](fixed-collections.md) sits directly in a component without any wrapping.

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

The idiomatic place to dispose entity-owned pointers is an `OnRemoved` reactive handler — see [Cleanup is manual](heap-allocation-rules.md#cleanup-is-manual).

!!! warning
    Forgetting to dispose pointers causes memory leaks. Trecs detects leaks at world shutdown in debug builds.

## Pointers in jobs

Use native pointer types (`NativeUniquePtr<T>` / `NativeSharedPtr<T>`) in jobs and call `Get` with a `NativeWorldAccessor`:

```csharp
ref NativeData data = ref nativeShared.Get(in nativeWorld);
```

`NativeUniquePtr<T>` is non-copyable — copying it inside a job is a `TRECS110` / `TRECS111` analyzer error. Pass it `ref` or move it explicitly.

## Heap types

Under the hood, Trecs maintains several heaps. You don't usually interact with them by name — you call the matching `Alloc…` method on `HeapAccessor`.

| Heap | Lifetime | Use case |
|------|----------|----------|
| `UniqueHeap` | Until disposed | Long-lived unique managed data |
| `SharedHeap` | Until ref count reaches zero | Shared managed data |
| `NativeUniqueHeap` | Until disposed | Long-lived unique unmanaged data |
| `NativeSharedHeap` | Until ref count reaches zero | Shared unmanaged data |
| `FrameScopedUniqueHeap` | Current fixed frame | Temporary per-frame data |
| `FrameScopedSharedHeap` | Current fixed frame | Temporary shared per-frame data |
| `FrameScopedNativeUniqueHeap` | Current fixed frame | Temporary native per-frame data |
| `FrameScopedNativeSharedHeap` | Current fixed frame | Temporary shared native per-frame data |

Frame-scoped heaps automatically clean up at the end of each fixed update — no manual disposal needed. They can only be allocated from `Input` or `Unrestricted` accessors (not `Fixed` or `Variable`); see [Accessor Roles](accessor-roles.md#capability-matrix).

## See also

- [Sample 10 — Pointers](../samples/10-pointers.md): unique and shared managed pointers stored on entities.
- [Sample 17 — Blob Storage](../samples/17-blob-storage.md): the seeder pattern with stable `BlobId` for shared assets.
