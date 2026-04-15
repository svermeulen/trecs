# Serialization

Trecs provides a full serialization framework for saving and restoring world state. This is the foundation for [Recording & Playback](recording-and-playback.md).

## Overview

The serialization system has two layers:

1. **SerializerRegistry** — maps types to serializers
2. **EcsStateSerializer** — serializes/deserializes entire world state (entities, components, heaps)

## SerializerRegistry

Register serializers for your types:

```csharp
var serialization = TrecsSerialization.Create();

// Core types are registered automatically (primitives, math types, ECS types)
// Register custom serializers for your types:
serialization.Registry.Register<MyData>(new MyDataSerializer());
```

### ISerializer\<T\>

Implement this interface for custom serialization:

```csharp
public class MyDataSerializer : ISerializer<MyData>
{
    public void Serialize(in MyData value, ISerializationWriter writer)
    {
        writer.WriteInt(value.X);
        writer.WriteFloat(value.Y);
    }

    public MyData Deserialize(ISerializationReader reader)
    {
        return new MyData
        {
            X = reader.ReadInt(),
            Y = reader.ReadFloat(),
        };
    }
}
```

### Built-in Serializers

Trecs includes serializers for:

- **Primitives** — `bool`, `string`, enums
- **Collections** — `List<T>`, `T[]`, `Dictionary<K,V>`, `HashSet<T>`, `NativeList<T>`, `NativeArray<T>`
- **Blitting** — `BlitSerializer` for unmanaged structs (copies raw bytes)
- **Special** — `DeprecatedSerializer` (skips data), `SkipSerializer`, `DefaultValueSerializer`

### TypeId

Components and other serialized types need a `[TypeId]` for stable identification:

```csharp
[TypeId(12345)]
public struct Health : IEntityComponent
{
    public float Current;
    public float Max;
}
```

Type IDs must be unique and stable across versions. Changing a type ID breaks deserialization of saved data.

## EcsStateSerializer

Serializes the complete world state — all entities, components, sets, and heap data:

```csharp
// Serialize
var writer = new BinarySerializationWriter();
ecsStateSerializer.Serialize(world, writer);
byte[] data = writer.ToArray();

// Deserialize
var reader = new BinarySerializationReader(data);
ecsStateSerializer.Deserialize(world, reader);
```

## Delta Serialization

For types that benefit from delta compression, implement `ISerializerDelta<T>`:

```csharp
public class MyDeltaSerializer : ISerializerDelta<MyData>
{
    public void SerializeDelta(in MyData current, in MyData baseline,
        ISerializationWriter writer) { ... }

    public MyData DeserializeDelta(in MyData baseline,
        ISerializationReader reader) { ... }
}
```

## BlobCache Serialization

Heap data (blobs) is serialized separately from component data. The `BlobCache` manages loading and caching via pluggable `IBlobStore` implementations:

```csharp
new WorldBuilder()
    .AddBlobStore(new BlobStoreInMemory())
    // ...
```

See [Heap](heap.md) for details on blob stores.
