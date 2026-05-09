# Recording & Playback

!!! note "When you need this page"
    Read this if you want **deterministic replay**, **networked rollback**, **save games beyond a single snapshot**, or **debug-replay tooling**. For just save/load of a single world snapshot, the [Snapshots section](#snapshots-save-games) is the only part you need.

Trecs supports full-state snapshots, deterministic recording/playback, sparse desync-detection checksums, and full-state anchors and user snapshots — the features behind networked rollback, save games, debugging, and QA replay tooling.

This page assumes you have read [Serialization](serialization.md), which covers `SerializerRegistry` and `WorldStateSerializer`.

## Overview

Four pieces, composed independently:

| Type | Purpose |
|---|---|
| `SnapshotSerializer` | Capture a single full-state snapshot and write it to a stream/file. Restore later. |
| `BundleRecorder` | Capture a span of fixed frames as a self-contained `RecordingBundle`: the initial snapshot, recorded inputs, sparse per-frame checksums, optional auto-anchors, and optional user snapshots. |
| `BundlePlayer` | Replay a `RecordingBundle` against a live world: restore the bundle's initial snapshot, feed its captured inputs, and verify per-frame checksums to surface desyncs. |
| `RecordingBundleSerializer` | Read and write `RecordingBundle` instances to streams or files. |

You can use any subset. Save-game projects only need
`SnapshotSerializer`; deterministic-replay tooling needs all four.

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

The `Samples/Serialization/Common/Scripts/SerializationFactory.cs`
helper bundles this up as a single `SerializationFactory.CreateAll(world)`
call and is a good copy-into-your-project starting point.

## Snapshots (save games)

```csharp
// Save the current world state to a file (creates parent directories).
SnapshotMetadata metadata = snapshots.SaveSnapshot(version: 1, filePath: "save.snap");

// ...later, possibly across editor restarts...

// Restore that saved state into the live world.
SnapshotMetadata loaded = snapshots.LoadSnapshot("save.snap");
```

Stream overloads exist for both calls if you need to write/read from
somewhere other than a file (e.g. a network socket or in-memory buffer).

`SnapshotSerializer.PeekMetadata(stream)` (or `(path)`) reads only the
snapshot header without restoring full state — handy for "Last saved at
frame X" displays in a save-slot UI.
`RecordingBundleSerializer.PeekHeader(stream)` (or `(path)`) is the
equivalent for replay-list tooling that wants to show a bundle's frame
range or schema version without loading the full bundle.

### `SnapshotMetadata`

The returned `SnapshotMetadata` carries:

- `Version` — the schema version you passed to `SaveSnapshot`
- `FixedFrame` — the world's fixed frame at capture time
- `BlobIds` — references to all heap blobs the snapshot relies on

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

`Start` captures an initial full-state snapshot of the world at the
current fixed frame and starts streaming captures into the recorder.
`Stop` produces a fully-populated `RecordingBundle` and resets internal
state — the recorder is reusable; `Start` can be called again.

The recorder runs as an `IInputHistoryLocker` while active, so the
input queue's normal cleanup won't prune frames the recording covers.

### `BundleRecorderSettings`

| Field | Default | Meaning |
|---|---|---|
| `Version` | `1` | User-defined schema version stamped on the produced bundle. |
| `AnchorIntervalSeconds` | `30f` | Wall-clock seconds (in simulation time) between auto-placed anchor snapshots. Anchors are full-state snapshots used as desync-recovery points at runtime and as scrub points in the editor's Trecs Player window. |
| `ChecksumFrameInterval` | `30` | Capture a checksum every N fixed frames during recording. Smaller = catches desyncs closer to where they happen; larger = less per-frame cost. Must be `>= 1`. |
| `ChecksumFlags` | `SerializationFlags.IsForChecksum` | Flags passed to the checksum serializer. Required when any user serializer branches on writer flags (e.g. to exclude non-deterministic state from checksums) — playback recomputes with the same flags via the bundle header. |

### `RecordingBundle`

The bundle returned by `recorder.Stop()` (and persisted by
`RecordingBundleSerializer`) is self-contained:

- `Header` — `BundleHeader` with `Version`, `StartFixedFrame`, `EndFixedFrame`, `FixedDeltaTime`, `ChecksumFlags`, and the heap `BlobIds` the bundle's snapshots reference.
- `InitialSnapshot` — `SnapshotSerializer` payload bytes for the world state at `StartFixedFrame`.
- `InitialSnapshotChecksum` — checksum of that initial state, used to verify the snapshot deserializes back to identical state.
- `InputQueue` — `EntityInputQueue` payload bytes covering the recorded frame range.
- `Checksums` — sparse per-frame world-state checksums (`DenseDictionary<int, uint>`), used by `BundlePlayer.Tick` for desync detection.
- `Anchors` — auto-placed full-state snapshots, ordered by frame. Doubles as runtime desync-recovery points and as scrub anchors in the editor.
- `Snapshots` — user-placed labeled full-state snapshots, ordered by frame. Surfaced in the recorder UI's timeline; survive Save/Load.

`CaptureAnchorAtCurrentFrame` and `CaptureSnapshotAtCurrentFrame` let
you place markers manually, outside the auto-cadence. Anchors and
snapshots are independent: deleting a snapshot never removes an
auto-anchor that happens to share a frame.

!!! note "`ChecksumFlags`"
    Set `ChecksumFlags` (or rely on the default of
    `SerializationFlags.IsForChecksum`) when any of your custom
    serializers branches on `ISerializationWriter.Flags` to exclude
    non-deterministic state from checksums. The flags are stored on
    `BundleHeader.ChecksumFlags` and replayed automatically by
    `BundlePlayer`, so the verification path sees exactly the same
    flags the recording path did.

## Playback

`BundlePlayer` consumes a `RecordingBundle` produced by `BundleRecorder`
(loaded straight from memory or via `RecordingBundleSerializer.Load`).
Playback runs three steps: restore the bundle's initial snapshot and
arm the per-frame check, run the simulation, and verify checksums on
each fixed update.

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

`Start` throws `SerializationException` if the post-deserialization
world's checksum disagrees with `RecordingBundle.InitialSnapshotChecksum`
— that points at a *serialization* defect (a custom serializer that
doesn't round-trip byte-identically), distinct from a *simulation*
desync (caught later by `Tick`).

### `BundlePlaybackState`

`BundlePlayer.State` exposes the lifecycle as an enum:

- `Idle` — no playback active.
- `Playing` — playback is running, and per-frame checksum checks have not (yet) failed.
- `Desynced` — a checksum mismatch was detected; subsequent `Tick` calls return `default` until `Stop` is called.

`IsPlaying` and `HasDesynced` are convenience accessors. `DesyncedFrame`
returns the frame at which the first mismatch was detected, or `null`
when consistent. `Bundle` returns the bundle currently being played
(`null` when idle).

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

Most frames have no recorded checksum (the recorder samples them at
`ChecksumFrameInterval`) — those return `default`.

## Desync recovery via anchors

When `BundlePlayer.Tick` reports a desync, the bundle's
`Anchors` list lets you recover by snapping back to the most recent
anchor before the desync frame:

1. Pick the latest `BundleAnchor` with `FixedFrame <= desyncFrame`.
2. Restore it via `SnapshotSerializer.LoadSnapshot(new MemoryStream(anchor.Payload))`.
3. Resume.

Anchors carry a `Checksum` field for verification, and runtime tooling
(e.g. the Trecs Player window in the editor) treats them as scrub
points for jump-to-frame navigation.

## Determinism requirements

For replay to actually replay (no desyncs), the simulation must be
deterministic:

1. **Enable deterministic submission:**
   ```csharp
   new WorldSettings { RequireDeterministicSubmission = true }
   ```
2. **Use deterministic RNG:**
   ```csharp
   new WorldSettings { RandomSeed = 42 }
   ```
   Always use `World.Rng` / `World.FixedRng` — never `UnityEngine.Random` or `System.Random`.
3. **Isolate inputs.** Use the [Input System](input-system.md) to queue player inputs. During playback, recorded inputs are replayed instead of live input — `BundlePlayer.Start` disables every input-phase system via `EnableChannel.Playback`.
4. **Use sort keys in jobs.** When using `NativeWorldAccessor` in parallel jobs, provide deterministic sort keys:
   ```csharp
   nativeWorld.AddEntity<MyTag>(sortKey: (uint)entityId);
   ```
5. **Avoid non-determinism.** No `DateTime.Now`, no `Dictionary` iteration order dependencies, no floating-point non-determinism from uncontrolled thread scheduling.

## Disposal

`SnapshotSerializer`, `BundleRecorder`, `BundlePlayer`, and
`RecordingBundleSerializer` all implement `IDisposable`. The recorder
and player gracefully end any in-flight recording or playback (with a
warning log) if disposed mid-operation, so the typical pattern is to
add them to your existing dispose chain:

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
