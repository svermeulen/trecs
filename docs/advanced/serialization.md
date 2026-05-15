# Serialization

Components are unmanaged structs, so they round-trip to bytes automatically — the framework treats them as plain memory. The one case where you need to write any serialization code is when a component points at a **managed** type (a `class`, or a struct with managed fields) via the [heap](heap.md). Examples: `SharedPtr<MyClass>`, `UniquePtr<MyClass>`, anything whose payload contains a `string`, `List<T>`, or another reference type.

For those types you register an `ISerializer<T>` against the world's [`SerializerRegistry`](#registering-the-registry). For everything else there is nothing to do — the [Trecs Player window](../editor-windows/player.md) records, scrubs, and replays world state without any setup beyond the world itself.

## Registering the registry

Every `World` owns a `SerializerRegistry`, pre-populated with all built-in primitive, math, ECS, and recording-metadata serializers. Add your own via `world.SerializerRegistry` after `WorldBuilder.Build()` and before `world.Initialize()`:

```csharp
var world = new WorldBuilder()
    .AddTemplate(myTemplate)
    .Build();

// Register one ISerializer<T> per managed type you store on the heap:
world.SerializerRegistry.RegisterSerializer<PatrolRouteSerializer>();
world.SerializerRegistry.RegisterSerializer<TrailHistorySerializer>();

world.Initialize();
```

You can also register them up front on the builder:

```csharp
var world = new WorldBuilder()
    .AddTemplate(myTemplate)
    .RegisterSerializer<PatrolRouteSerializer>()
    .RegisterSerializer<TrailHistorySerializer>()
    .BuildAndInitialize();
```

If the type happens to be blittable (no managed fields), you can skip the custom serializer entirely — register the built-in `BlitSerializer<T>` against the registry directly (`world.SerializerRegistry.RegisterSerializer<BlitSerializer<T>>()`). For enums, register `EnumSerializer<T>` the same way. The common primitives (`int`, `float`, `string`, `Vector3`, `quaternion`, …) are pre-registered.

## Authoring a custom serializer

Implement `ISerializer<T>` with paired `Serialize` and `Deserialize` methods. The binary format is purely positional — `Deserialize` must call the same readers in the same order as `Serialize` calls writers.

Take the `PatrolRoute` shape from [Sample 10 — Pointers](../samples/10-pointers.md): a managed class holding a `List<Vector3>` and a speed, allocated via `World.Heap.AllocShared` and stored on a component as `SharedPtr<PatrolRoute>`.

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

## See also

- [Heap](heap.md) — pointer types and which kinds of data need to live on the heap.
- [Trecs Player Window](../editor-windows/player.md) — uses the registered serializers to record, scrub, and replay world state.
- [Sample 10 — Pointers](../samples/10-pointers.md) — full example of `SharedPtr` / `UniquePtr` over managed types.
