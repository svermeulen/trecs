# Trecs Collections

!!! warning "Experimental"
    `TrecsList<T>` and the broader Trecs-collection family are experimental — the API and backing-storage details may change in future 0.x releases.

Trecs ships a small set of growable, unmanaged collections you can store directly on a component without a `NativeUniquePtr` wrapper. Their backing storage lives on a dedicated heap that Trecs walks during snapshot / record / playback, so the contents serialize automatically alongside the component bytes.

Use them when you need a dynamically-sized per-entity collection and:

- [`FixedList<N>`](../advanced/fixed-collections.md) is too restrictive (no clear upper bound, or the worst-case bound wastes too much memory).
- A `NativeUniquePtr<NativeList<T>>` would also work but you don't want the boilerplate of registering inner-collection disposal in an `OnRemoved` handler and remembering to dispose the inner allocation before the pointer.

Currently the library exposes one type — `TrecsList<T>`. More may follow.

## `TrecsList<T>`

A growable list of unmanaged values. The struct itself is a 4-byte handle (a `PtrHandle`), so it lives inline on the component; the backing array sits on the world's shared `NativeChunkStore`.

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

A `(world, initialCapacity)` overload taking a `WorldAccessor` is also available.

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

Read/Write surfaces come in two flavors:

- **Managed** — `Read(WorldAccessor)` / `Write(WorldAccessor)` return `TrecsListRead<T>` / `TrecsListWrite<T>`, main-thread `ref struct` views. `TrecsListWrite.Add` auto-grows the backing buffer, updating the wrapper's cached data pointer in place.
- **Native** — `Read(in NativeWorldAccessor)` / `Write(in NativeWorldAccessor)` return `NativeTrecsListRead<T>` / `NativeTrecsListWrite<T>`, Burst-safe views usable inside `[BurstCompile]` jobs. These do **not** auto-grow; pre-size with `list.EnsureCapacity(World, minCapacity)` on the main thread before scheduling.

Both flavors expose `Count`, `Capacity`, and indexers (`ref readonly` on Read, `ref` on Write), plus `Add` / `RemoveAt` / `RemoveAtSwapBack` / `Clear` on Write. Both carry an `AtomicSafetyHandle` so the job-safety walker catches cross-job read/write conflicts at schedule time.

Inside a Burst job:

```csharp
[ForEachEntity(typeof(MyTag))]
[WrapAsJob]
static void Execute(ref CCollisionPairBuffer buf, in NativeWorldAccessor world)
{
    var write = buf.Value.Write(world);
    write.Add(/* ... */);
}
```

`Write(...)` is a `ref this` instance method, so the call site needs a writable reference to the pointer struct itself — same rule as [the gotcha for `NativeUniquePtr`](../guides/gotchas.md#mutating-a-nativeuniqueptrt-needs-write-access-to-the-owning-component).

### Capacity

The managed `TrecsListWrite.Add` auto-grows on overflow (doubling). Inside a Burst job, `NativeTrecsListWrite.Add` throws on overflow instead — pre-size with `list.EnsureCapacity(World, minCapacity)` on the main thread before scheduling, or to avoid mid-frame reallocation in a hot loop. The list header lives at a stable address across reallocations.

### Disposing

Like other heap-backed types, `TrecsList<T>` is not auto-freed when an entity is removed. Dispose entity-owned lists in an `OnRemoved` observer — see [Pointers — cleanup is manual for entity-owned pointers](pointers.md#cleanup-is-manual-for-entity-owned-pointers).

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
| [`FixedList<N>`](../advanced/fixed-collections.md) | The element count has a known, small upper bound (≤ 256) and every entity uses roughly the same amount. Inline, no disposal. |
| `TrecsList<T>` | Variable per-entity count, unmanaged elements. Round-trips through snapshots / recording with no custom serializer. |
| `NativeUniquePtr<NativeList<T>>` | You need Unity's `NativeList<T>` API specifically, or want to share the allocation with non-Trecs code. Requires an explicit dispose for the inner collection — see [Storing native collections](pointers.md#storing-native-collections). |
| `UniquePtr<List<T>>` | Managed element types (classes, strings). Main-thread only. Needs a registered `ISerializer<T>` for the payload — see [Serialization](serialization.md). |
