# Serialization

Components are unmanaged structs, so they round-trip to bytes automatically — the framework treats them as plain memory. The two cases where you write serialization code yourself are:

1. A component points at a **managed** type (a `class`, or a struct with managed fields) via the [heap](heap.md) — e.g. `SharedPtr<MyClass>`, `UniquePtr<MyClass>`, anything whose payload contains a `string`, `List<T>`, or another reference type. Register an `ISerializer<T>` against [`world.SerializerRegistry`](#registering-the-registry).

2. A component holds an **unmanaged native container** like `UnsafeHashMap<TKey,TValue>` or `UnsafeList<T>`, or you want to **skip** serializing the component entirely (transient single-frame state, runtime handles to reset on load). Register an `IComponentArraySerializer<T>` against [`world.ComponentArraySerializerRegistry`](#custom-component-array-serialization).

For everything else there is nothing to do — the [Trecs Player window](../editor-windows/player.md) records, scrubs, and replays world state without any setup beyond the world itself.

## Registering the registry

Every `World` owns a `SerializerRegistry`, pre-populated with all built-in primitive, math, ECS, and recording-metadata serializers. Add your own via `world.SerializerRegistry` after `WorldBuilder.Build()` and before `world.Initialize()`:

```csharp
var world = new WorldBuilder()
    .AddTemplate(myTemplate)
    .Build();

// Register one ISerializer<T> per managed type you store on the heap:
world.SerializerRegistry.RegisterSerializer(new PatrolRouteSerializer());
world.SerializerRegistry.RegisterSerializer(new TrailHistorySerializer());

world.Initialize();
```

You can also register them up front on the builder:

```csharp
var world = new WorldBuilder()
    .AddTemplate(myTemplate)
    .RegisterSerializer(new PatrolRouteSerializer())
    .RegisterSerializer(new TrailHistorySerializer())
    .BuildAndInitialize();
```

If the type happens to be blittable (no managed fields), you can skip the custom serializer entirely — register the built-in `BlitSerializer<T>` against the registry directly (`world.SerializerRegistry.RegisterSerializer(new BlitSerializer<T>())`). For enums, register `EnumSerializer<T>` the same way. The common primitives (`int`, `float`, `string`, `Vector3`, `quaternion`, …) are pre-registered.

## Authoring a custom serializer

Implement `ISerializer<T>` with paired `Serialize` and `Deserialize` methods. The binary format is purely positional — `Deserialize` must call the same readers in the same order as `Serialize` calls writers.

As a concrete example, take a managed class holding a list of waypoints, allocated via `SharedPtr.Alloc(world.Heap, blobId, value)` and stored on a component as `SharedPtr<PatrolRoute>`:

```csharp
public class PatrolRoute
{
    public List<Vector3> Waypoints;
    public float Speed;
}

public sealed class PatrolRouteSerializer : ISerializer<PatrolRoute>
{
    public void Serialize(in PatrolRoute value, ISerializationWriter writer)
    {
        writer.Write("count", value.Waypoints.Count);
        foreach (var waypoint in value.Waypoints)
        {
            writer.Write("waypoint", waypoint);
        }
        writer.Write("speed", value.Speed);
    }

    public void Deserialize(ref PatrolRoute value, ISerializationReader reader)
    {
        value ??= new PatrolRoute { Waypoints = new() };
        var count = reader.Read<int>("count");
        value.Waypoints.Clear();
        for (int i = 0; i < count; i++)
        {
            value.Waypoints.Add(reader.Read<Vector3>("waypoint"));
        }
        value.Speed = reader.Read<float>("speed");
    }
}
```

!!! note "Field names are debug-only"
    The `name` arguments to `writer.Write` / `reader.Read` are **not** persisted — they only surface in debug memory tracking and desync diagnostics. Reads must match writes by position, not by name.

If `value` is a struct with managed fields rather than a class, the `??=` line is unnecessary — `Deserialize` just populates the fields directly.

## Versioning

If the type's shape changes between releases, bump a version on the write side and branch on `reader.Version` to keep loading older saves:

```csharp
public void Deserialize(ref PatrolRoute value, ISerializationReader reader)
{
    value ??= new PatrolRoute { Waypoints = new() };
    var count = reader.Read<int>("count");
    value.Waypoints.Clear();
    for (int i = 0; i < count; i++)
    {
        value.Waypoints.Add(reader.Read<Vector3>("waypoint"));
    }
    value.Speed = reader.Read<float>("speed");

    // v2 added the Reverse flag; older saves default it to false.
    value.Reverse = reader.Version >= 2 && reader.Read<bool>("reverse");
}
```

`ISerializationWriter.Version` is available on the write side for the symmetric branch.

## Custom component-array serialization

`ISerializer<T>` covers managed types on the heap. `IComponentArraySerializer<T>` covers the other side: overriding how the framework serializes the **array of values** for one specific component type during snapshots, recordings, and checksums.

Three common reasons to register one:

- **Skip a component entirely.** Transient single-frame state — collision scratchpads, broadphase buffers, debug overlays — that you don't want to persist and don't want contributing to determinism checksums.
- **Reset on load instead of restoring.** Runtime handles (physics simulations, audio voices, third-party engine objects) where serializing the internal state isn't possible or meaningful; on deserialize you reinitialize a fresh handle.
- **Walk a native container.** A component holding an `UnsafeHashMap<K,V>`, `UnsafeList<T>`, or similar — these can't be byte-blitted because their backing memory lives outside the component struct.

Every `World` exposes a `ComponentArraySerializerRegistry` for this:

```csharp
world.ComponentArraySerializerRegistry.Register(new MyPhysicsWorldSerializer());
```

The interface hands you a `NativeList<T>` view of the component values for the group being serialized — a familiar, Burst-compatible Unity Collections type. The framework owns the entry count (every component array in a group has the same length, one entry per entity) and writes it for you, so you only deal with per-element data:

```csharp
public interface IComponentArraySerializer<T> where T : unmanaged, IEntityComponent
{
    void Serialize(NativeList<T> values, ISerializationWriter writer);
    void Deserialize(NativeList<T> values, int requiredCount, ISerializationReader reader);
}
```

On `Serialize`, `values.Length` equals the current entity count. On `Deserialize`, `values.Length` is the **live world's** current count and `requiredCount` is the count the array must have on return — you decide how to reconcile (preserve in place when counts match, resize-and-default-init, dispose-and-rebuild, etc.). The dispatcher asserts `values.Length == requiredCount` after you return.

### Skipping a component entirely

The skip case is common enough to ship as a one-liner. `SkipComponentSerializer<T>` skips writing/reading the component's values and leaves the live array's contents untouched on load:

```csharp
using Trecs.Serialization;

world.ComponentArraySerializerRegistry.Register(new SkipComponentSerializer<CBroadphaseScratch>());
world.ComponentArraySerializerRegistry.Register(new SkipComponentSerializer<CDebugOverlay>());
```

Because no value bytes are written, the component contributes nothing of substance to the stream — and therefore nothing to the checksum hash — so determinism checks won't desync on values that vary between runs. This is the simplest way to keep transient per-frame state out of recordings.

The framework still writes the entry **count** (a single `int`) for every component array, and `SkipComponentSerializer<T>` asserts on load that `requiredCount` matches the live array's length. A snapshot taken when the group had _N_ entities must be restored into a live world where the group also has _N_ entities. If the counts diverge — e.g. the user added more entities of that template between save and load — the deserialize throws. Without this check the skipped component array would silently desync from the rest of the group, corrupting entity-component lookups downstream.

`SkipComponentSerializer<T>` is right for the in-session save/restore case, where the same entities exist on both sides and you want to preserve their runtime state. For fresh-load-from-disk scenarios — where the live world starts empty and the snapshot brings the entities into existence — use `DefaultValueComponentSerializer<T>` instead:

```csharp
world.ComponentArraySerializerRegistry.Register(new DefaultValueComponentSerializer<CMyTransient>());
```

It also writes nothing per element, but on load **resizes** the array to `requiredCount` and zero-inits every entry. The values still contribute nothing to the stream's bytes (the checksum hash skips them too), but they're regenerated to `default(T)` on load rather than asserting on a count mismatch. Use it for components whose entries the runtime re-initializes from elsewhere (system init, OnAdded handlers, the next tick's update) — _not_ for components whose live runtime state is the source of truth.

### Reusing an `ISerializer<T>` for per-element dispatch

When you already have an `ISerializer<T>` for a component type — for example a versioned per-element format used elsewhere — wrap it in `PerEntityComponentArraySerializer<T>` rather than copying the logic into a new `IComponentArraySerializer<T>`:

```csharp
world.ComponentArraySerializerRegistry.Register(
    new PerEntityComponentArraySerializer<CMyComponent>(new MyComponentSerializer()));
```

The framework still owns the count; the inner `ISerializer<T>` is called once per entity. This costs one virtual call per entity vs. one per array — fine for snapshots and recordings, but prefer a dedicated `IComponentArraySerializer<T>` (one loop, no inner virtual dispatch) on hot rollback paths over large groups.

### Conditional skipping (checksum-only)

To exclude a component's values from checksum streams only — while still serializing them normally in snapshots and recordings — check `writer.Flags` inside `Serialize` and write nothing when `IsForChecksum` is set:

```csharp
public void Serialize(NativeList<CMyComponent> values, ISerializationWriter writer)
{
    if (writer.HasFlag(SerializationFlags.IsForChecksum))
    {
        return; // skip per-element data; the framework still writes the count
    }
    for (int i = 0; i < values.Length; i++) { /* normal writes */ }
}
```

Checksum streams are never deserialized, so the read side doesn't need a symmetric branch.

### Reset on load

When a component wraps a runtime handle that should be reinitialized rather than restored, write nothing on `Serialize` and reset the existing instance in place on `Deserialize` — keeping the handle's native allocations intact:

```csharp
public sealed class PhysicsWorldSerializer : IComponentArraySerializer<CPhysicsWorld>
{
    public void Serialize(NativeList<CPhysicsWorld> values, ISerializationWriter writer) { }

    public void Deserialize(
        NativeList<CPhysicsWorld> values,
        int requiredCount,
        ISerializationReader reader)
    {
        // Component lives on a single global-template entity, so both
        // values.Length and requiredCount are always 1. Reset the
        // PhysicsWorld in place rather than discarding it and creating a
        // new one — this preserves the handle's existing native allocations.
        values.ElementAt(0).PhysicsWorld.Reset();
    }
}
```

### Walking a native container

For a component with an `UnsafeHashMap<K,V>` (or any other native container the framework can't blit), iterate it explicitly. Use the same write-then-read pattern you'd use for `ISerializer<T>`, but operating over each element of the `NativeList<T>`:

```csharp
public partial struct CCollisionGroup : IEntityComponent
{
    public CollisionTagSet TagSet;
    public UnsafeHashMap<CollisionPair, CollisionInfo> Existing;
}

public sealed class CollisionGroupSerializer : IComponentArraySerializer<CCollisionGroup>
{
    public void Serialize(NativeList<CCollisionGroup> values, ISerializationWriter writer)
    {
        for (int i = 0; i < values.Length; i++)
        {
            ref readonly var group = ref values.ElementAt(i);
            writer.Write("tagSet", group.TagSet);
            writer.Write("pairCount", group.Existing.Count);
            foreach (var kv in group.Existing)
            {
                writer.Write("pair", kv.Key);
                writer.Write("info", kv.Value);
            }
        }
    }

    public void Deserialize(
        NativeList<CCollisionGroup> values,
        int requiredCount,
        ISerializationReader reader)
    {
        // Dispose any native containers the existing elements own before we
        // overwrite the slots, then rebuild from the stream.
        for (int i = 0; i < values.Length; i++)
        {
            values[i].Existing.Dispose();
        }
        values.Resize(requiredCount, NativeArrayOptions.ClearMemory);
        for (int i = 0; i < requiredCount; i++)
        {
            ref var group = ref values.ElementAt(i);
            group.TagSet = reader.Read<CollisionTagSet>("tagSet");
            var pairCount = reader.Read<int>("pairCount");
            group.Existing = new UnsafeHashMap<CollisionPair, CollisionInfo>(pairCount, Allocator.Persistent);
            for (int p = 0; p < pairCount; p++)
            {
                var key = reader.Read<CollisionPair>("pair");
                var info = reader.Read<CollisionInfo>("info");
                group.Existing.Add(key, info);
            }
        }
    }
}
```

The same versioning rules apply as for `ISerializer<T>`: read what you wrote in the same order, branch on `reader.Version` if the layout has changed.

## See also

- [Heap](heap.md) — pointer types and which kinds of data need to live on the heap.
- [Trecs Player Window](../editor-windows/player.md) — uses the registered serializers to record, scrub, and replay world state.
- [Sample 10 — Pointers](../samples/10-pointers.md) — `UniquePtr<TrailHistory>` plus a registered `TrailHistorySerializer` so the trail round-trips through snapshots / recording.
- [Sample 15 — Blob Storage](../samples/15-blob-storage.md) — `SharedPtr<ColorPalette>` with stable `BlobId`s (the sample itself doesn't take snapshots, but the same `ISerializer<T>` pattern from Sample 10 would apply).
