# Serialization

Trecs ships an optional binary serialization framework (`com.trecs.serialization` package) for full ECS world state. It is the foundation for [Recording & Playback](recording-and-playback.md) and for save/load systems.

## Quick start

```csharp
// 1. Build a registry with all Trecs serializers pre-registered.
var registry = TrecsSerialization.CreateSerializerRegistry();

// 2. Register any custom serializers your game needs (see below).
//    Blittable component types are auto-handled — no registration needed.
//    registry.RegisterSerializer<MyCustomSerializer>();

// 3. Compose only the handlers you actually use.
var worldStateSerializer = new WorldStateSerializer(world);
var snapshots = new SnapshotSerializer(worldStateSerializer, registry, world);
```

For a save-game flow stop here and use `SnapshotSerializer.SaveSnapshot` / `LoadSnapshot`. For deterministic record/replay also construct `BundleRecorder`, `BundlePlayer`, and `RecordingBundleSerializer` (see [Recording & Playback](recording-and-playback.md)).

`SerializerRegistry` is a generic serializer independent of Trecs — it exposes generic `RegisterBlit<T>`, `RegisterEnum<T>`, `RegisterSerializer<TSerializer>` and can be used standalone in non-ECS code.

## Authoring a custom serializer

Components are unmanaged structs and serialize automatically via the built-in blit serializer. You only need a custom `ISerializer<T>` if you are storing custom types on the [heap](heap.md).

```csharp
public sealed class HighScoreTableSerializer : ISerializer<HighScoreTable>
{
    public void Serialize(in HighScoreTable value, ISerializationWriter writer)
    {
        writer.Write("count", value.Entries.Count);
        foreach (var entry in value.Entries)
        {
            writer.Write("name", entry.Name);     // managed string
            writer.Write("score", entry.Score);
        }
    }

    public void Deserialize(ref HighScoreTable value, ISerializationReader reader)
    {
        value ??= new HighScoreTable();
        var count = reader.Read<int>("count");
        value.Entries.Clear();
        for (int i = 0; i < count; i++)
        {
            value.Entries.Add(new HighScoreEntry
            {
                Name = reader.Read<string>("name"),
                Score = reader.Read<int>("score"),
            });
        }
    }
}

// Register before constructing any handler:
registry.RegisterSerializer<HighScoreTableSerializer>();
```

!!! note "Field names are discarded"
    The `name` arguments to `writer.Write` / `reader.Read` are **not** persisted — they exist for debug memory tracking and to help debug desyncs. The binary format is purely positional: reads must occur in the same order as writes.

### Blit registrations

Unmanaged value types can be registered as a fast raw-byte copy:

```csharp
registry.RegisterBlit<MyStruct>();              // serialize only
registry.RegisterBlit<MyStruct>(includeDelta: true);  // also enable delta encoding
registry.RegisterEnum<MyEnum>();                // for enum types
```

## Writer / reader flags

Every `ISerializationWriter` / `ISerializationReader` carries a `long Flags` bitmask available inside a custom serializer. Flags are passed at the top level (`WriteAll(value, version, includeTypeChecks, flags: …)`, `StartWrite(version, includeTypeChecks, flags: …)`) and propagate to every nested serializer.

Typical use: excluding non-deterministic state from checksums. Define a constant, set it from the recording's `checksumFlags` parameter, then branch inside your serializer:

```csharp
public static class MyAppFlags
{
    public const long ForChecksum = 1L << 0;
}

public void Serialize(in Npc value, ISerializationWriter writer)
{
    writer.Write("position", value.Position);
    writer.Write("hp", value.Hp);

    // Skip the animator fingerprint when computing checksums — it's
    // driven by a non-deterministic presentation layer.
    if ((writer.Flags & MyAppFlags.ForChecksum) == 0)
    {
        writer.Write("animatorHash", value.AnimatorHash);
    }
}
```

### Versioned custom serializers

The `version` integer is exposed to custom serializers via `ISerializationWriter.Version` (during write) and `ISerializationReader.Version` (during read). The deserializer can recognize older saves and read them with the layout they were written in. Bump the version on every layout change, and a single serializer can keep handling all prior versions:

```csharp
public sealed class HighScoreTableSerializer : ISerializer<HighScoreTable>
{
    public void Serialize(in HighScoreTable value, ISerializationWriter writer)
    {
        // The writer always emits the current (latest) layout. The version stored
        // in the file header records *which* layout this is, so old files saved by
        // earlier code are still readable below.
        writer.Write("count", value.Entries.Count);
        foreach (var entry in value.Entries)
        {
            writer.Write("name", entry.Name);
            writer.Write("score", entry.Score);
            writer.Write("timestamp", entry.Timestamp);   // added in v2
        }
    }

    public void Deserialize(ref HighScoreTable value, ISerializationReader reader)
    {
        value ??= new HighScoreTable();
        var count = reader.Read<int>("count");
        value.Entries.Clear();
        for (int i = 0; i < count; i++)
        {
            var entry = new HighScoreEntry
            {
                Name = reader.Read<string>("name"),
                Score = reader.Read<int>("score"),
                // v1 saves don't contain timestamp; default it instead of reading.
                Timestamp = reader.Version >= 2
                    ? reader.Read<long>("timestamp")
                    : 0,
            };
            value.Entries.Add(entry);
        }
    }
}
```

