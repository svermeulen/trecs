# Heap

Components must be unmanaged structs, so they can't hold classes, arrays, or other managed references. The **heap** system provides pointer types that let components reference data stored outside the component buffer.

## Pointer Types

| Type | Ownership | Data Type | Burst-Safe |
|------|-----------|-----------|------------|
| `UniquePtr<T>` | Single owner | Managed (`class`) | No |
| `SharedPtr<T>` | Reference counted | Managed (`class`) | No |
| `NativeUniquePtr<T>` | Single owner | Unmanaged (`struct`) | Yes |
| `NativeSharedPtr<T>` | Reference counted | Unmanaged (`struct`) | Yes |

## Allocating Pointers

Access the heap via `WorldAccessor.Heap`:

```csharp
// Managed unique pointer
UniquePtr<MyData> unique = World.Heap.AllocUnique(new MyData());

// Managed shared pointer
SharedPtr<MyData> shared = World.Heap.AllocShared(new MyData());

// Native unique pointer (Burst-safe)
NativeUniquePtr<NativeData> nativeUnique = World.Heap.AllocNativeUnique(new NativeData());

// Native shared pointer (Burst-safe)
NativeSharedPtr<NativeData> nativeShared = World.Heap.AllocNativeShared(new NativeData());
```

## Reading Pointer Data

```csharp
// Managed pointers
MyData data = unique.Get(World.Heap);
MyData data = shared.Get(World.Heap);

// Native pointers
ref NativeData data = ref nativeUnique.Get(World.Heap);
ref NativeData data = ref nativeShared.Get(World.Heap);

// Safe access
if (shared.TryGet(World.Heap, out MyData data)) { ... }
```

## Storing Pointers in Components

Pointer structs are unmanaged, so they can be stored as component fields:

```csharp
public struct MeshReference : IEntityComponent
{
    public SharedPtr<Mesh> Mesh;
}

// Set during entity creation
world.AddEntity<MyTag>()
    .Set(new MeshReference { Mesh = World.Heap.AllocShared(mesh) });

// Read in a system
ref readonly MeshReference meshRef = ref World.Component<MeshReference>(entity).Read;
Mesh mesh = meshRef.Mesh.Get(World.Heap);
```

## Shared Pointers and Reference Counting

`SharedPtr<T>` and `NativeSharedPtr<T>` use reference counting. Multiple components can reference the same data:

```csharp
SharedPtr<MyData> original = World.Heap.AllocShared(new MyData());
SharedPtr<MyData> clone = original.Clone(World.Heap);  // Increments ref count
```

## Disposing Pointers

Pointers must be manually disposed when no longer needed:

```csharp
unique.Dispose(World.Heap);
shared.Dispose(World.Heap);  // Decrements ref count, frees if zero
```

!!! warning
    Forgetting to dispose pointers causes memory leaks. Trecs detects leaks at world shutdown in debug builds.

## Pointers in Jobs

Use native pointer types with resolvers:

```csharp
// Get resolvers (main thread)
NativeSharedPtrResolver resolver = World.Heap.NativeSharedPtrResolver;

// In a job:
ref NativeData data = ref nativeShared.Get(in resolver);
```

Or via `NativeWorldAccessor`:

```csharp
ref NativeData data = ref nativeShared.Get(in nativeWorldAccessor);
```

## BlobPtr and BlobCache

For data that can be loaded from external sources (files, Addressables), use `BlobPtr<T>`:

```csharp
// Allocate with a BlobId
NativeSharedPtr<MyBlob> ptr = World.Heap.AllocNativeShared<MyBlob>(blobId);
```

### IBlobStore

Register custom blob stores for loading data:

```csharp
new WorldBuilder()
    .AddBlobStore(new BlobStoreInMemory())  // Built-in in-memory store
    .AddBlobStore(myCustomStore)            // Custom loader
    // ...
```

`IBlobStore` implementations can load data from files, addressables, network, etc.

## Heap Types

Under the hood, Trecs maintains several heaps:

| Heap | Lifetime | Use Case |
|------|----------|----------|
| `UniqueHeap` | Until disposed | Long-lived unique managed data |
| `SharedHeap` | Until ref count reaches zero | Shared managed data |
| `NativeUniqueHeap` | Until disposed | Long-lived unique unmanaged data |
| `NativeSharedHeap` | Until ref count reaches zero | Shared unmanaged data |
| `FrameScopedUniqueHeap` | Current fixed frame | Temporary per-frame data |
| `FrameScopedSharedHeap` | Current fixed frame | Temporary shared per-frame data |
| `FrameScopedNativeUniqueHeap` | Current fixed frame | Temporary native per-frame data |
| `FrameScopedNativeSharedHeap` | Current fixed frame | Temporary shared native per-frame data |

Frame-scoped heaps automatically clean up at the end of each fixed update — no manual disposal needed.
