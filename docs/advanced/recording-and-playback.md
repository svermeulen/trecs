# Recording & Playback

!!! note "When you need this page"
    Read this for **deterministic replay**, **networked rollback**, **save games beyond a single snapshot**, or **debug-replay tooling**. For save/load of a single world snapshot, only the [Snapshots section](#snapshots-save-games) applies.

Assumes you've read [Serialization](serialization.md), which covers `SerializerRegistry` and `WorldStateSerializer`.

## Overview

Four pieces, composed independently:

| Type | Purpose |
|---|---|
| `SnapshotSerializer` | Capture a full-state snapshot to a stream/file; restore later. |
| `BundleRecorder` | Capture a span of fixed frames as a self-contained `RecordingBundle`: initial snapshot, inputs, sparse per-frame checksums, optional auto-anchors, optional user snapshots. |
| `BundlePlayer` | Replay a `RecordingBundle` against a live world: restore the initial snapshot, feed captured inputs, verify per-frame checksums to surface desyncs. |
| `RecordingBundleSerializer` | Read/write `RecordingBundle` instances to streams or files. |

Use any subset. Save-game projects only need `SnapshotSerializer`; deterministic-replay tooling needs all four.

## Composing the pieces

```csharp
var registry = TrecsSerialization.CreateSerializerRegistry();
var worldStateSerializer = new WorldStateSerializer(world);

var snapshots         = new SnapshotSerializer(worldStateSerializer, registry, world);
var bundleSerializer  = new RecordingBundleSerializer(registry);
var recorderSettings  = new BundleRecorderSettings();
var recorder          = new BundleRecorder(world, worldStateSerializer, registry, recorderSettings, snapshots);
var player            = new BundlePlayer(world, worldStateSerializer, registry, snapshots);

recorder.Initialize();
player.Initialize();
```

`Samples/Serialization/Common/Scripts/SerializationFactory.cs` wraps this as `SerializationFactory.CreateAll(world)` — a good copy-into-your-project starting point.

## Snapshots (save games)

```csharp
// Save the current world state to a file (creates parent directories).
SnapshotMetadata metadata = snapshots.SaveSnapshot(version: 1, filePath: "save.snap");

// ...later, possibly across editor restarts...

// Restore that saved state into the live world.
SnapshotMetadata loaded = snapshots.LoadSnapshot("save.snap");
```

Stream overloads exist for both calls (network sockets, in-memory buffers).

`SnapshotSerializer.PeekMetadata(stream)` (or `(path)`) reads only the snapshot header without restoring state — for "Last saved at frame X" displays in save-slot UI. `RecordingBundleSerializer.PeekHeader` is the equivalent for replay-list tooling that needs a bundle's frame range or schema version without a full load.

### `SnapshotMetadata`

- `Version` — schema version passed to `SaveSnapshot`
- `FixedFrame` — world's fixed frame at capture time
- `BlobIds` — references to heap blobs the snapshot relies on

## Recording

```csharp
// Configure capture cadence (anchor interval, checksum interval, ...).
var recorderSettings = new BundleRecorderSettings
{
    Version = 1,
    AnchorIntervalSeconds = 30f,
    ChecksumFrameInterval = 30,
    // ChecksumFlags defaults to SerializationFlags.IsForChecksum so any
    // serializer that branches on writer.HasFlag(IsForChecksum) skips
    // non-deterministic state automatically.
};

// Snapshot the world and start recording inputs + checksums.
recorder.Start();

// ... game runs for some number of fixed frames ...
//
// Optional: drop a marker at an interesting moment.
recorder.CaptureAnchorAtCurrentFrame();              // unlabeled — recovery / scrub anchor
recorder.CaptureSnapshotAtCurrentFrame("just before the bug");  // labeled — surfaced in UI

// Stop and persist.
RecordingBundle bundle = recorder.Stop();
bundleSerializer.Save(bundle, "recording.trec");
```

`Start` captures a full-state snapshot at the current fixed frame and begins streaming captures. `Stop` produces a populated `RecordingBundle` and resets internal state — the recorder is reusable.

While active, the recorder acts as an `IInputHistoryLocker`, so the input queue's cleanup won't prune frames the recording covers.

### `BundleRecorderSettings`

| Field | Default | Meaning |
|---|---|---|
| `Version` | `1` | User-defined schema version stamped on the bundle. |
| `AnchorIntervalSeconds` | `30f` | Simulation seconds between auto-placed anchor snapshots. Anchors serve as runtime desync-recovery points and as scrub points in the editor's Trecs Player window. |
| `ChecksumFrameInterval` | `30` | Capture a checksum every N fixed frames. Smaller = catches desyncs closer to the cause; larger = lower per-frame cost. Must be `>= 1`. |
| `ChecksumFlags` | `SerializationFlags.IsForChecksum` | Flags passed to the checksum serializer. Needed when a user serializer branches on writer flags (e.g. to exclude non-deterministic state) — playback recomputes with the same flags via the bundle header. |

### `RecordingBundle`

The bundle returned by `recorder.Stop()` (persisted by `RecordingBundleSerializer`) is self-contained:

- `Header` — `BundleHeader` with `Version`, `StartFixedFrame`, `EndFixedFrame`, `FixedDeltaTime`, `ChecksumFlags`, and heap `BlobIds` referenced by the bundle's snapshots.
- `InitialSnapshot` — `SnapshotSerializer` payload bytes for the world state at `StartFixedFrame`.
- `InitialSnapshotChecksum` — checksum of that initial state; verifies the snapshot round-trips byte-identically.
- `InputQueue` — `EntityInputQueue` payload bytes for the recorded frame range.
- `Checksums` — sparse per-frame world-state checksums (`DenseDictionary<int, uint>`); used by `BundlePlayer.Tick` for desync detection.
- `Anchors` — auto-placed full-state snapshots, ordered by frame. Used as runtime desync-recovery points and as editor scrub anchors.
- `Snapshots` — user-placed labeled full-state snapshots, ordered by frame. Surfaced in the recorder UI's timeline; survive Save/Load.

`CaptureAnchorAtCurrentFrame` and `CaptureSnapshotAtCurrentFrame` place markers manually outside the auto-cadence. Anchors and snapshots are independent: deleting a snapshot never removes an auto-anchor on the same frame.

The `ChecksumFlags` from settings are stored on `BundleHeader.ChecksumFlags` and replayed automatically by `BundlePlayer`, so verification sees the same flags as recording.

## Playback

`BundlePlayer` consumes a `RecordingBundle` (in memory or via `RecordingBundleSerializer.Load`). Three steps: restore the initial snapshot and arm the per-frame check, run the simulation, verify checksums each fixed update.

```csharp
// Load the bundle from disk.
RecordingBundle bundle = bundleSerializer.Load("recording.trec");

// Restore initial state into the live world, hydrate the input queue,
// and arm the per-frame desync check. This also disables input-phase
// systems via EnableChannel.Playback so live input doesn't fight the
// recorded inputs.
player.Start(bundle);

// During each fixed update, check for desyncs.
PlaybackTickResult result = player.Tick();
if (result.DesyncDetected)
{
    Debug.LogError(
        $"Desync at this frame: expected {result.ExpectedChecksum}, " +
        $"got {result.ActualChecksum}");
}

// When done:
player.Stop();
```

`Start` throws `SerializationException` if the deserialized world's checksum disagrees with `RecordingBundle.InitialSnapshotChecksum` — that's a *serialization* defect (a custom serializer not round-tripping byte-identically), distinct from a *simulation* desync (caught later by `Tick`).

### `BundlePlaybackState`

`BundlePlayer.State`:

- `Idle` — no playback active.
- `Playing` — playback running; checksums haven't failed.
- `Desynced` — checksum mismatch detected; subsequent `Tick` calls return `default` until `Stop`.

`IsPlaying` and `HasDesynced` are convenience accessors. `DesyncedFrame` returns the first mismatch frame, or `null` when consistent. `Bundle` returns the playing bundle (`null` when idle).

### `PlaybackTickResult`

```csharp
public struct PlaybackTickResult
{
    public uint? ExpectedChecksum;   // null on frames with no recorded checksum
    public uint? ActualChecksum;     // null on frames with no recorded checksum
    public bool ChecksumVerified;    // ExpectedChecksum.HasValue
    public bool DesyncDetected;      // checksums diverged
}
```

Most frames have no recorded checksum (the recorder samples at `ChecksumFrameInterval`) — those return `default`.

## Desync recovery via anchors

When `BundlePlayer.Tick` reports a desync, recover by snapping back to the most recent anchor before the desync frame:

1. Pick the latest `BundleAnchor` with `FixedFrame <= desyncFrame`.
2. Restore via `SnapshotSerializer.LoadSnapshot(new MemoryStream(anchor.Payload))`.
3. Resume.

Anchors carry a `Checksum` field for verification, and runtime tooling (e.g. the editor's Trecs Player window) treats them as scrub points for jump-to-frame navigation.

## Determinism requirements

Replay only works if the simulation is deterministic:

1. **Enable deterministic submission:**
   ```csharp
   new WorldSettings { RequireDeterministicSubmission = true }
   ```
2. **Use deterministic RNG:**
   ```csharp
   new WorldSettings { RandomSeed = 42 }
   ```
   Always use `World.Rng` / `World.FixedRng` — never `UnityEngine.Random` or `System.Random`.
3. **Isolate inputs.** Queue player inputs via the [Input System](../core/input-system.md). During playback, recorded inputs replace live input — `BundlePlayer.Start` disables every input-phase system via `EnableChannel.Playback`.
4. **Use sort keys in jobs.** When using `NativeWorldAccessor` in parallel jobs, provide deterministic sort keys:
   ```csharp
   nativeWorld.AddEntity<MyTag>(sortKey: (uint)entityId);
   ```
5. **Avoid non-determinism.** No `DateTime.Now`, no `Dictionary` iteration-order dependencies, no floating-point non-determinism from uncontrolled thread scheduling.

## Disposal

`SnapshotSerializer`, `BundleRecorder`, `BundlePlayer`, and `RecordingBundleSerializer` all implement `IDisposable`. The recorder and player gracefully end any in-flight operation (with a warning log) if disposed mid-flight, so add them to your existing dispose chain:

```csharp
disposables.Add(player.Dispose);
disposables.Add(recorder.Dispose);
disposables.Add(bundleSerializer.Dispose);
disposables.Add(snapshots.Dispose);
```

## See also

- [Serialization](serialization.md) — `SerializerRegistry`, `WorldStateSerializer`, custom serializers.
- [Binary Format Reference](binary-format.md) — on-disk layout of bundles and snapshots.
- [Sample 11 — Snake](../samples/11-snake.md) — full record/replay flow wired to keyboard hotkeys.
