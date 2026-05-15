# Trecs Collections

Trecs ships a small set of growable, unmanaged collections you can store directly on a component without a `NativeUniquePtr` wrapper. Their backing storage lives on a dedicated heap that Trecs walks during snapshot / record / playback, so the contents serialize automatically alongside the component bytes.

Use them when you need a dynamically-sized per-entity collection and:

- [`FixedList<N>`](fixed-collections.md) is too restrictive (no clear upper bound, or the worst-case bound wastes too much memory).
- A `NativeUniquePtr<NativeList<T>>` would also work but you don't want the boilerplate of registering inner-collection disposal in an `OnRemoved` handler and remembering to dispose the inner allocation before the pointer.

Currently the library exposes one type — `TrecsList<T>`. More may follow.

## `TrecsList<T>`

A growable list of unmanaged values. The struct itself is a 4-byte handle (a `PtrHandle`), so it lives inline on the component; the backing array sits on the world's `TrecsListHeap`.

```csharp
public partial struct CCollisionPairBuffer : IEntityComponent
{
    public TrecsList<CollisionPair> Value;
}
```

`T` must be `unmanaged`.

### Allocating

```csharp
var list = TrecsList.Alloc<CollisionPair>(World, initialCapacity: 16);

World.AddEntity<MyTag>()
    .Set(new CCollisionPairBuffer { Value = list });
```

The `(world.Heap, initialCapacity)` overload also exists for callers that already hold a `HeapAccessor`.

### Reading and writing

Like the native pointer types, access goes through typed `Read(...)` / `Write(...)` wrappers so Unity's job-safety system can track conflicts:

```csharp
// Main thread
ref readonly var entry = ref list.Read(World)[0];

var write = list.Write(World);
write.Add(new CollisionPair(a, b));
write[0] = updatedEntry;
write.RemoveAtSwapBack(2);
write.Clear();
```

`Read` returns a `TrecsListRead<T>` (`Count`, `Capacity`, `ref readonly` indexer). `Write` returns a `TrecsListWrite<T>` adding `Add`, `RemoveAt`, `RemoveAtSwapBack`, `Clear`, and a `ref` indexer. Both carry an `AtomicSafetyHandle` so cross-job read/write conflicts are caught at schedule time.

Inside a Burst job, pass a resolver instead:

```csharp
[ForEachEntity(typeof(MyTag))]
[WrapAsJob]
static void Execute(ref CCollisionPairBuffer buf, in NativeWorldAccessor world)
{
    var write = buf.Value.Write(world.TrecsListResolver);
    write.Add(/* ... */);
}
```

`Write(...)` is a `ref this` instance method, so the call site needs a writable reference to the pointer struct itself — same rule as [the gotcha for `NativeUniquePtr`](../guides/gotchas.md#mutating-a-nativeuniqueptrt-needs-write-access-to-the-owning-component).

### Capacity

`Add` reallocates when `Count == Capacity`. To avoid mid-frame reallocation in a hot loop, call `list.EnsureCapacity(World, minCapacity)` ahead of time. The list header lives at a stable address even across reallocations, so a cached `TrecsListWrite<T>` stays valid after a grow.

### Disposing

Like other heap-backed types, `TrecsList<T>` is not auto-freed when an entity is removed. Dispose entity-owned lists in an `OnRemoved` observer — see [Heap — cleanup is manual for entity-owned pointers](heap.md#cleanup-is-manual-for-entity-owned-pointers).

```csharp
[ForEachEntity]
void OnEntityRemoved(in CCollisionPairBuffer buf)
{
    buf.Value.Dispose(World);
}
```

Forgetting to dispose leaks the backing storage; Trecs reports leaks at world shutdown in DEBUG builds.

## When to reach for each option

| Use | When |
|---|---|
| [`FixedList<N>`](fixed-collections.md) | The element count has a known, small upper bound (≤ 256) and every entity uses roughly the same amount. Inline, no disposal. |
| `TrecsList<T>` | Variable per-entity count, unmanaged elements. Round-trips through snapshots / recording with no custom serializer. |
| `NativeUniquePtr<NativeList<T>>` | You need Unity's `NativeList<T>` API specifically, or want to share the allocation with non-Trecs code. Requires an explicit dispose for the inner collection — see [Storing native collections](heap.md#storing-native-collections). |
| `UniquePtr<List<T>>` | Managed element types (classes, strings). Main-thread only. Needs a registered `ISerializer<T>` for the payload — see [Serialization](serialization.md). |
