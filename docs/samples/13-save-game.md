# 13 — Save Game

A small Sokoban puzzle showing the `SnapshotSerializer` file API in the classic save-game use case: save before a risky move, reload if you get stuck.

**Source:** `Samples/13_SaveGame/`

## What it does

Push every box onto a target square. Boxes can be pushed but never pulled, so a box shoved into a corner is stuck forever — load a snapshot to undo.

| Key | Action |
|---|---|
| WASD / Arrow keys | Move / push |
| F1 / F2 / F3 | Save to slot 1 / 2 / 3 |
| F5 / F6 / F7 | Load slot 1 / 2 / 3 |

Slot files live under `{Application.persistentDataPath}/SaveGame/slot{N}.snap` and persist across editor restarts. The HUD shows each slot's last-save timestamp and a "Boxes on targets" counter.

## Serialization setup

The sample uses the full `SerializationFactory.CreateAll(world)` helper (from `Samples/Common/Scripts/`), but a save-game-only project does not need the recording handlers. The leanest stack:

```csharp
var registry = new SerializerRegistry();
TrecsSerialization.RegisterCoreSerializers(registry);
TrecsSerialization.RegisterTrecsSerializers(registry);
// (skip RegisterRecordingSerializers — no record/playback needed)

var worldStateSer = new WorldStateSerializer(world);
var snapshots = new SnapshotSerializer(worldStateSer, registry, world);
```

## Save / Load

The controller uses `SnapshotSerializer`'s file overloads directly:

```csharp
// Save
var metadata = snapshots.SaveSnapshot(version: 1, filePath: slot.FilePath);

// Load — restores the entire ECS state (walls, boxes, targets, player)
var metadata = snapshots.LoadSnapshot(slot.FilePath);
```

Because level geometry is wall/target entities rather than a static prefab, **loading a snapshot reverts the whole level**, not just the moveable pieces. That's the ECS-first design: anything you made part of the world is part of a save.

Both methods also accept a `Stream` if you want to encrypt, compress, or upload the bytes.

### Peeking metadata and version checks

`SnapshotSerializer.PeekMetadata(path)` reads only the header (schema version, current frame, blob ids, connection count) without rehydrating the world. Two common uses:

- **Save-slot UIs** that show "last saved at frame 1234" without paying the deserialization cost.
- **Version guards** before a full load. The `version` int passed to `SaveSnapshot` is preserved on `SnapshotMetadata.Version`, so you can detect saves from an incompatible build:

```csharp
var header = snapshots.PeekMetadata(path);
if (header.Version != currentSchemaVersion)
{
    ShowIncompatibleSaveDialog();
    return;
}
snapshots.LoadSnapshot(path);
```

Trecs does not interpret `version` itself — bumping it on breaking schema changes is your convention.

## Concepts introduced

- **`SnapshotSerializer.SaveSnapshot(path)` / `LoadSnapshot(path)`** — the file-based save/load API.
- **Multiple save slots** — each file is an independent snapshot.
- **Full-state round-trip** — static-seeming geometry stored as ECS state saves and restores like dynamic state.
- **`PeekMetadata(path)`** — header-only read for save-slot UIs.

## Related

- [Serialization](../advanced/serialization.md) — `SerializerRegistry`, custom `ISerializer<T>` authoring.
- [Recording & Playback](../advanced/recording-and-playback.md) — the other major use case for the serialization package, covered by Sample 11 — Snake.
