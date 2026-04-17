# Heap

Components must be unmanaged structs, so they can't hold classes, arrays, or other managed references. The **heap** system provides pointer types that let components reference data stored outside the component buffer.

## Pointer Types

| Type | Ownership | Mutability | Data Type | Burst-Safe |
|------|-----------|------------|-----------|------------|
| `UniquePtr<T>` | Single owner | Mutable | Managed (`class`) | No |
| `SharedPtr<T>` | Reference counted | Immutable | Managed (`class`) | No |
| `NativeUniquePtr<T>` | Single owner | Mutable | Unmanaged (`struct`) | Yes |
| `NativeSharedPtr<T>` | Reference counted | Immutable | Unmanaged (`struct`) | Yes |

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

Use native pointer types in jobs via `NativeWorldAccessor`:

```csharp
ref NativeData data = ref nativeShared.Get(in nativeWorldAccessor);
```

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

Frame-scoped heaps automatically clean up at the end of each fixed update — no manual disposal needed. Note that during [recording playback](recording-and-playback.md), frame-scoped data may persist longer than a single fixed frame.
