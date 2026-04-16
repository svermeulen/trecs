# Serialization

!!! note
    Serialization types live in the `com.trecs.serialization` package, which must be installed separately from `com.trecs.core`.

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
        writer.Write("X", value.X);
        writer.Write("Y", value.Y);
    }

    public void Deserialize(ref MyData value, ISerializationReader reader)
    {
        reader.Read("X", ref value.X);
        reader.Read("Y", ref value.Y);
    }
}
```

### Built-in Serializers

Trecs includes serializers for:

- **Primitives** — `bool`, `string`, enums
- **Collections** — `List<T>`, `T[]`, `Dictionary<K,V>`, `HashSet<T>`, `NativeList<T>`, `NativeArray<T>`
- **Blitting** — `BlitSerializer` for unmanaged structs (copies raw bytes)
- **Special** — `DeprecatedSerializer` (skips data), `SkipSerializer`, `DefaultValueSerializer`

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
    public void SerializeDelta(in MyData value, in MyData baseValue,
        ISerializationWriter writer) { ... }

    public void DeserializeDelta(ref MyData value, in MyData baseValue,
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
