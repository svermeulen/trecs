# Serialization

!!! note
    Serialization types live in the `com.trecs.serialization` package, which must be installed separately from `com.trecs.core`.

Trecs ships an optional binary serialization framework for full ECS world state. It is the foundation for [Recording & Playback](recording-and-playback.md) and for save/load systems.

## Overview

Compose just the pieces you need:

- **`SerializerRegistry`** — maps types to their serializers.
- **`WorldStateSerializer`** — reads/writes the entire ECS world (components, sets, heaps, entity handles).
- **`SnapshotSerializer`** — captures and restores full state snapshots.
- **`RecordingHandler` / `PlaybackHandler`** — capture and replay simulation history (covered in the next page).

The static helper `TrecsSerialization` provides preset registrations so the common path is one line. Users that only need partial setup (e.g. save-game-only projects that skip the recording machinery) can call individual `Register*Serializers` helpers instead.

## Quick start

```csharp
// 1. Build a registry pre-populated with all Trecs serializers.
var registry = TrecsSerialization.CreateSerializerRegistry();

// 2. Register any custom serializers your game needs (see below).
//    All blittable component types are auto-handled — no registration needed.
//    registry.RegisterSerializer<MyCustomSerializer>();

// 3. Compose only the handlers you actually use.
var worldStateSerializer = new WorldStateSerializer(world);
var snapshots = new SnapshotSerializer(worldStateSerializer, registry, world);
```

For a save-game flow you stop here and use `SnapshotSerializer.SaveSnapshot`/`LoadSnapshot`. For deterministic record/replay also construct `RecordingHandler` and `PlaybackHandler` (see the [Recording & Playback](recording-and-playback.md) page).

## Granular registration

If you do not want everything, use the building-block helpers:

```csharp
var registry = new SerializerRegistry();
TrecsSerialization.RegisterCoreSerializers(registry);   // primitives, math types
TrecsSerialization.RegisterTrecsSerializers(registry);  // ECS internals
// (skip RegisterRecordingSerializers — save-game-only project)
```

`SerializerRegistry` itself contains only generic `RegisterBlit<T>`, `RegisterEnum<T>`, `RegisterSerializer<TSerializer>` etc. — it has no knowledge of Trecs ECS types and can be used standalone.

## Authoring a custom serializer

Most components are unmanaged structs and serialize automatically via the built-in blit serializer — no extra code required. You only need a custom `ISerializer<T>` for types that hold managed references (lists, dictionaries, strings) or where you want a non-default encoding.

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
    The `name` arguments passed to `writer.Write` / `reader.Read` are **not** persisted in the binary stream — they exist only for debug memory tracking (`GetMemoryReport`) and as self-documentation. The binary format is purely positional: reads must occur in the exact same order as writes. Renaming a field is a no-op on disk; reordering reads or adding/removing one without bumping the format version will silently corrupt deserialization.

### Blit registrations

Unmanaged value types can be registered as a fast raw-byte copy:

```csharp
registry.RegisterBlit<MyStruct>();              // serialize only
registry.RegisterBlit<MyStruct>(includeDelta: true);  // also enable delta encoding
registry.RegisterEnum<MyEnum>();                // for enum types
```

Delta-capable types must implement `IEquatable<T>`.

## Extending world state with non-ECS data

If your game has state that lives outside the ECS world (scripting VMs, external caches), subclass `WorldStateSerializer` to write/read your additional chunks:

```csharp
public sealed class MyGameStateSerializer : WorldStateSerializer
{
    readonly LuaInterpreter _lua;

    public MyGameStateSerializer(World world, LuaInterpreter lua) : base(world)
    {
        _lua = lua;
    }

    public override void SerializeState(ISerializationWriter writer)
    {
        base.SerializeState(writer);
        writer.Write("luaState", _lua.CaptureSnapshot());
    }

    public override void DeserializeState(ISerializationReader reader)
    {
        base.DeserializeState(reader);
        _lua.RestoreSnapshot(reader.Read<string>("luaState"));
    }
}

// Pass the subclass to SnapshotSerializer / RecordingHandler / PlaybackHandler.
var worldStateSer = new MyGameStateSerializer(world, lua);
var snapshots = new SnapshotSerializer(worldStateSer, registry, world);
```

## Buffer reuse

`SerializationBuffer` is the shared write/read buffer used internally by every handler. Most users never touch it directly — `SnapshotSerializer.SaveSnapshot(stream)` and friends manage it for you. Power users who need to drive the binary reader/writer themselves can construct one explicitly:

```csharp
using var buffer = new SerializationBuffer(registry);
buffer.WriteAll(value, version: 1, includeTypeChecks: true);
buffer.ResetMemoryPosition();
var roundTripped = buffer.ReadAll<MyType>();
```

## Determinism notes

- Always use `World.Rng` / `World.FixedRng` in fixed update — never `UnityEngine.Random` or `System.Random`.
- Set `RequireDeterministicSubmission = true` in `WorldSettings` if you intend to record and replay.

### `WorldSettings.AssertNoTimeInFixedPhase`

For deterministic-lockstep workloads (e.g. RTS netcode) where the simulation must produce bit-identical results across machines, set `AssertNoTimeInFixedPhase = true`:

```csharp
var settings = new WorldSettings
{
    RequireDeterministicSubmission = true,
    AssertNoTimeInFixedPhase = true,
};
```

Trecs guarantees deterministic scheduling, iteration, and entity ordering, but it **cannot** guarantee deterministic floating-point math across hardware. Reading continuous time values (`DeltaTime`, `ElapsedTime`, `FixedDeltaTime`, `FixedElapsedTime`) during the fixed-update phase is a common source of drift, because accumulated floating-point error diverges across machines.

With the flag enabled:

- Accessing any of those four properties on `WorldAccessor` during fixed update **throws**.
- In Burst jobs (where exceptions are unavailable), `NativeWorldAccessor.DeltaTime` / `NativeWorldAccessor.ElapsedTime` are populated with `float.NaN` so any arithmetic that uses them produces visibly broken output instead of silent desync.

Use `World.FixedFrame` — a discrete tick counter — as your time source in fixed update instead. Variable-update systems are unaffected.

See [Recording & Playback](recording-and-playback.md) for the determinism-sensitive lifecycle and desync-detection workflow.

## Threading

All `SnapshotSerializer`, `RecordingHandler`, and `PlaybackHandler` methods
are **main-thread only**. The underlying `SerializationBuffer` and the
blit fast-path use a shared static byte buffer, and every read/write
path asserts `UnityThreadUtil.IsMainThread`. Do not call save/load from
a background thread.

## Binary format stability

The binary layout is **version-sensitive** and not forward-compatible:

- Adding, removing, or reordering fields on a blittable component changes
  the byte layout, invalidating every previously saved snapshot or
  recording that used the old shape.
- The `version` integer you pass to `SaveSnapshot(version, …)` /
  `StartRecording(version, …)` is stored in the file header and exposed
  on `SnapshotMetadata.Version` / `RecordingMetadata.Version`. Trecs does
  not interpret it — bumping `version` on a breaking change is a
  convention you own.
- Use `SnapshotSerializer.PeekMetadata(path)` (or `(stream)`) to inspect
  the saved version before committing to a full `LoadSnapshot`, and
  surface a user-facing error for incompatible saves.

!!! note "Two different 'versions'"
    Don't confuse the user `version` above with Trecs's own internal
    `FormatVersion` byte written at the start of every payload by
    `SerializationHeaderUtil`. The format version describes the layout
    of the header itself (magic bytes + fields); it is bumped only when
    Trecs changes the framing, and users never set it. Your
    `version` parameter is the schema version of your game's serialized
    data — bump it whenever *your* serializers change shape.

## Writer / reader flags

Every `ISerializationWriter`/`ISerializationReader` carries a `long Flags`
bitmask you can consult from inside a custom serializer. Flags are
threaded in at the top level (`WriteAll(value, version, includeTypeChecks,
flags: …)`, `StartWrite(version, includeTypeChecks, flags: …)`) and
propagate to every nested serializer.

Typical use: excluding non-deterministic state from checksums. Define a
constant, set it from the recording's `checksumFlags` parameter, then
branch inside your serializer:

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

`RecordingHandler` stores the flags on `RecordingMetadata.ChecksumFlags`
and `PlaybackHandler` passes them back in automatically during checksum
verification, so recording and playback always agree on what was
included.

### Versioned custom serializers

The `version` integer you pass to `SaveSnapshot` / `StartRecording` is stamped into the file header *and* exposed to custom serializers via `ISerializationWriter.Version` (during write) and `ISerializationReader.Version` (during read). Because the header records the version the file was written with, the deserializer can recognize older saves and read them with the layout they were written in. As long as you bump the version every time you change the on-disk layout, a single serializer can keep handling all prior versions:

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

The example only adds a field, but `Version` lets you handle removals and reorderings too — branch the deserializer on `reader.Version` and read the appropriate layout for each historical version. The constraint is that you have to keep the read-side code for every old version you still want to support, and bump `version` every time the layout changes.

If your game needs long-term save compatibility across deeper schema changes (renamed types, restructured aggregates, splitting one component into two), wrap the Trecs snapshot inside your own versioned format and perform migrations before calling `LoadSnapshot`.

For the exact on-disk layout (header fields, stream guards, endianness, integrity caveats), see the [Binary Format Reference](binary-format.md).

