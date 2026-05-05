# Recording & Playback

Trecs supports full-state snapshots, deterministic recording/playback, and checksum-based desync detection — the features behind networked rollback, save games, debugging, and QA replay tooling.

This page assumes you have read [Serialization](serialization.md), which covers `SerializerRegistry` and `WorldStateSerializer`.

## Overview

Three handlers, composed independently:

| Handler | Purpose |
|---|---|
| `SnapshotSerializer` | Capture a single full-state snapshot and write it to a stream/file. Restore later. |
| `RecordingHandler` | Capture every input + periodic checksums for a span of fixed frames, then write the recording to a stream/file. |
| `PlaybackHandler` | Replay a recording, verifying checksums per frame and surfacing desyncs. |

You can use any subset. Save-game projects only need `SnapshotSerializer`; debug-only QA tooling needs all three.

## Composing the handlers

```csharp
var registry = TrecsSerialization.CreateSerializerRegistry();
var worldStateSer = new WorldStateSerializer(world);

var snapshots = new SnapshotSerializer(worldStateSer, registry, world);
var recorder  = new RecordingHandler(worldStateSer, registry, world);
var playback  = new PlaybackHandler(worldStateSer, snapshots, registry, world);
```

## Snapshots (save games)

```csharp
// Save the current world state to a file (creates parent directories).
var metadata = snapshots.SaveSnapshot(version: 1, filePath: "save.bin");

// ...later, possibly across editor restarts...

// Restore that saved state into the live world.
var loaded = snapshots.LoadSnapshot("save.bin");
```

Stream overloads exist for both calls if you need to write/read from somewhere other than a file (e.g. a network socket or in-memory buffer).

`SnapshotSerializer.PeekMetadata(stream)` (or `(path)`) reads only the snapshot header without restoring full state — handy for "Last saved at frame X" displays in a save-slot UI. `RecordingHandler` exposes the same pair of overloads for replay-list tooling that wants to show duration, frame range, or schema version without loading a full recording.

### `SnapshotMetadata`

The returned `SnapshotMetadata` carries:

- `Version` — the schema version you passed to `SaveSnapshot`
- `FixedFrame` — the world's fixed frame at capture time
- `BlobIds` — references to all heap blobs the snapshot relies on

## Recording

```csharp
// Start capturing inputs + periodic checksums from the current frame.
recorder.StartRecording(
    version: 1,
    checksumsEnabled: true,
    checksumFrameInterval: 30
    // checksumFlags: 0  // optional — see note below
);

// ... game runs for some number of fixed frames ...

// Stop capturing and write the recording to disk.
RecordingMetadata metadata = recorder.EndRecording("recording.bin");
```

A `Stream` overload of `EndRecording` is also available.

`RecordingMetadata` exposes:

- `Version` — the schema version you passed to `StartRecording`
- `StartFixedFrame` / `EndFixedFrame` — frame range covered
- `ChecksumFlags` — flag bitmask that was active while computing checksums (replayed automatically during playback; see below)
- `Checksums` — the per-frame checksums captured during recording, used for desync detection during playback
- `BlobIds` — heap blobs the recording references

`StartRecording` requires `checksumFrameInterval >= 1`.

!!! note "`checksumFlags` (optional)"
    Pass `checksumFlags` when any of your custom serializers branches on
    `ISerializationWriter.Flags` (for example, to exclude non-deterministic
    state from checksums). The flags are stored on `RecordingMetadata` and
    replayed automatically by `PlaybackHandler`, so the verification path
    sees exactly the same flags the recording path did. Leave it at `0`
    if you don't use flags-sensitive serializers.

## Playback

Recording and playback are two halves of the same workflow. Playback typically follows three steps: load an initial-state snapshot to start from a known point, start playback against the recording stream, and call `TickPlayback()` once per fixed update to verify checksums.

```csharp
// (Optional but recommended) restore the snapshot captured when the recording started.
playback.LoadInitialState(
    snapshotPath: "snapshot.bin",
    expectedInitialChecksum: null
);

// Begin replaying recorded inputs.
playback.StartPlayback("recording.bin", new PlaybackStartParams
{
    Version = 1,
    InputsOnly = false,
});

// During each fixed update, check for desyncs.
PlaybackTickResult result = playback.TickPlayback();
if (result.DesyncDetected)
{
    Debug.LogError(
        $"Desync at this frame: expected {result.ExpectedChecksum}, " +
        $"got {result.ActualChecksum}");
}

// When done:
playback.EndPlayback();
```

### `PlaybackState`

`PlaybackHandler.State` exposes the lifecycle as an enum (`Idle`, `Playing`, `Desynced`). The `IsPlaying` and `HasDesynced` boolean accessors remain available for convenience. `PlaybackHandler.PlaybackMetadata` returns the `RecordingMetadata` of the recording currently being played — useful for UI / debug overlays that want to display the recording's frame range or schema version live.

### `PlaybackTickResult`

```csharp
public struct PlaybackTickResult
{
    public bool ChecksumVerified;   // a checksum was compared this frame
    public bool DesyncDetected;     // checksums diverged
    public uint? ExpectedChecksum;
    public uint? ActualChecksum;
}
```

### `InputsOnly` mode

Setting `PlaybackStartParams.InputsOnly = true` re-anchors the recorded input frame numbers to the world's *current* fixed frame at start-of-playback. Useful when you have already restored state via some other path (e.g. a deterministic replay from a different snapshot) and just want to inject the recorded inputs starting "now".

## Determinism requirements

For replay to actually replay (no desyncs), the simulation must be deterministic:

1. **Enable deterministic submission:**
   ```csharp
   new WorldSettings { RequireDeterministicSubmission = true }
   ```
2. **Use deterministic RNG:**
   ```csharp
   new WorldSettings { RandomSeed = 42 }
   ```
   Always use `World.Rng` / `World.FixedRng` — never `UnityEngine.Random` or `System.Random`.
3. **Isolate inputs.** Use the [Input System](input-system.md) to queue player inputs. During playback, recorded inputs are replayed instead of live input.
4. **Use sort keys in jobs.** When using `NativeWorldAccessor` in parallel jobs, provide deterministic sort keys:
   ```csharp
   nativeWorld.AddEntity<MyTag>(sortKey: (uint)entityId);
   ```
5. **Avoid non-determinism.** No `DateTime.Now`, no `Dictionary` iteration order dependencies, no floating-point non-determinism from uncontrolled thread scheduling.

## Disposal

All three handlers implement `IDisposable`. They will gracefully end any in-flight recording or playback (with a warning log) if disposed mid-operation, so the typical pattern is to add them to your existing dispose chain:

```csharp
disposables.Add(playback.Dispose);
disposables.Add(recorder.Dispose);
disposables.Add(snapshots.Dispose);
```

## See also

- [Serialization](serialization.md) — `SerializerRegistry`, `WorldStateSerializer`, custom serializers.
- [Binary Format Reference](binary-format.md) — on-disk layout of recordings and snapshots.
- [Sample 11 — Snake](../samples/11-snake.md) — full record/replay flow wired to keyboard hotkeys.
