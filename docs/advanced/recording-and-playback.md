# Recording & Playback

Trecs supports bookmarks (full state snapshots), deterministic recording/playback, and checksum-based desync detection — key features for networking, rollbacks, debugging, and QA.

## Overview

The recording system captures simulation state at regular intervals, allowing you to:

- **Record** a play session and replay it later
- **Detect desyncs** by comparing checksums during playback
- **Seek** to any point in a recording via bookmarks
- **Debug** issues by replaying exact sequences of events

## RecordingHandler

Records simulation state at fixed intervals:

```csharp
var recording = new RecordingHandler(
    gameStateSerializer,
    serializationServices,
    checksumCalculator: new RecordingChecksumCalculator(...)
);

// During game loop:
recording.OnTick(world);  // Captures state at configured intervals
```

### Checksums

Enable checksum calculation to verify determinism:

```csharp
var checksumCalc = new RecordingChecksumCalculator(
    ecsStateSerializer,
    serializerRegistry
);
```

Checksums are computed from the serialized world state and stored alongside recordings. During playback, checksums are recomputed and compared to detect desyncs.

## PlaybackHandler

Replays a recording, optionally detecting desyncs:

```csharp
var playback = new PlaybackHandler(
    gameStateSerializer,
    serializationServices
);

// Start playback
playback.Start(recordingData, new PlaybackStartParams { ... });

// Each tick:
PlaybackTickResult result = playback.OnTick(world);

if (result.DesyncDetected)
{
    // Simulation diverged from recording
}
```

### PlaybackTickResult

```csharp
// Check if playback detected a desync
if (result.DesyncDetected)
{
    Debug.LogError($"Desync: expected checksum {result.ExpectedChecksum}, got {result.ActualChecksum}");
}

// Check if a checksum was verified this tick
if (result.ChecksumVerified)
{
    Debug.Log("Checksum matched");
}
```

## Bookmarks

Bookmarks are full snapshots of world state at specific frames, enabling fast seeking:

```csharp
var bookmarkSerializer = new BookmarkSerializer(
    ecsStateSerializer,
    serializerRegistry
);
```

Bookmarks let you jump to any recorded frame without replaying from the beginning.

## Requirements for Deterministic Replay

For recordings to replay correctly, your simulation must be deterministic:

1. **Enable deterministic submission:**
   ```csharp
   new WorldSettings { RequireDeterministicSubmission = true }
   ```

2. **Use deterministic RNG:**
   ```csharp
   new WorldSettings { RandomSeed = 42 }
   ```
   Always use `World.Rng` / `World.FixedRng` — never `UnityEngine.Random` or `System.Random`.

3. **Isolate inputs:** Use the [Input System](input-system.md) to queue player inputs. During playback, recorded inputs are replayed instead of live input.

4. **Use sort keys in jobs:** When using `NativeWorldAccessor` in parallel jobs, provide deterministic sort keys:
   ```csharp
   nativeWorld.AddEntity<MyTag>(sortKey: (uint)entityId);
   ```

5. **Avoid non-determinism:** No `DateTime.Now`, no `Dictionary` iteration order dependencies, no floating-point non-determinism from uncontrolled thread scheduling.

## SerializationServices

Bundle all serialization components together:

```csharp
var services = new SerializationServices(
    registry: serializerRegistry,
    ecsStateSerializer: ecsStateSerializer,
    recordingHandler: recordingHandler,
    playbackHandler: playbackHandler
);
```

## IGameStateSerializer

Abstraction for complete game state serialization:

```csharp
var gameStateSerializer = new SimpleGameStateSerializer(ecsStateSerializer);
```

Implement `IGameStateSerializer` for custom state that lives outside the ECS world.
