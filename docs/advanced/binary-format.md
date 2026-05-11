# Binary Format Reference

This page documents the on-disk layout of Trecs binary payloads (snapshots and recording bundles). Most users never need it — the API in [Serialization](serialization.md) and [Recording & Playback](recording-and-playback.md) is self-contained. Read this page if you are:

- Building tooling that inspects save files without loading them into Unity.
- Diagnosing a payload-corruption bug.
- Implementing a migration from one `FormatVersion` to the next.

All offsets below are in bytes. Byte ordering is **mixed** — see "Endianness and portability" below. `int` is 4 bytes, `long` is 8 bytes, `bool` is 1 byte.

## Layered structure

Every payload has three nested layers, inner to outer:

1. **Values** — whatever the caller passed to `WriteAll<T>` / `SerializeState`.
2. **Framing** — bit-packed booleans + an end-of-payload marker added by `BinarySerializationWriter`.
3. **Header** — magic bytes + version fields added by `SerializationHeaderUtil`.

The header is always present. The framing is always present. The value section is caller-defined.

## Payload header (16 bytes)

Written by [`SerializationHeaderUtil.WriteHeader`](https://github.com/svermeulen/trecs/blob/main/UnityProject/Trecs/Assets/com.trecs.serialization/Scripts/Misc/SerializationHeaderUtil.cs) at byte 0 of every payload.

| Offset | Size | Field | Value |
|--------|------|-------|-------|
| 0      | 1    | `MagicByte0`     | `'T'` (0x54) |
| 1      | 1    | `MagicByte1`     | `'R'` (0x52) |
| 2      | 1    | `FormatVersion`  | 1 (current) |
| 3      | 4    | `version` (int32 LE) | Caller-supplied schema version |
| 7      | 8    | `flags` (int64 LE) | Caller-supplied bitmask |
| 15     | 1    | `includeTypeChecks` (bool) | 1 = type IDs are interleaved in the data section |

### `FormatVersion` vs. `version`

These are unrelated and must not be confused.

- `FormatVersion` is owned by Trecs. It describes the header layout itself. Trecs bumps it only when adding/removing/reordering header fields; current value is `1`. Mismatch throws — there is no forward-compat path, by design.
- `version` is owned by your game. It describes the *schema* of the serialized data inside the payload. Bump it whenever you change a component or custom-serializer format. Trecs surfaces it on `SnapshotMetadata.Version` / `BundleHeader.Version` but does not interpret it.

### `flags`

Application-level bitmask propagated to every serializer via `ISerializationWriter.Flags` / `ISerializationReader.Flags`. Typical use: excluding non-deterministic state from checksums (see [Writer/reader flags](serialization.md#writer-reader-flags)).

### `includeTypeChecks`

When `true`, the writer interleaves type IDs before each top-level value so the reader can assert it's reading what the writer wrote. Snapshots and recordings use `true`; most user-driven round trips do too. Disable only when you need the smallest payload and the reader trusts the stream end-to-end.

## Framing section

Immediately after the header.

### Bit-field prelude

Packed booleans emitted by `BitWriter`, consumed by `BitReader`. Count-prefixed: an int32 giving the number of packed bits, followed by `⌈bits/8⌉` bytes. Bit ordering is little-endian within each byte.

Serializers emit bit-level booleans for delta encodings: one bit per `(value == baseValue)` check, then the full value if changed. See `ISerializer<T>.SerializeDelta` / `ISerializerDelta<T>`.

### Data section

Sequential values, one per `Write(…)` call in writer order. The binary format is **purely positional** — field name strings passed to `Write` / `Read` are not persisted (they exist for memory-tracking reports and readability in custom code). Renaming a field is a no-op on disk; reordering reads is a silent corruption.

### End-of-payload marker

One byte `0x5E` (`SerializationConstants.EndOfPayloadMarker`). Distinguishes a valid end-of-payload from a truncated read. It catches only the tail — a bit-flip inside the data section is not detected.

## Snapshot payload (world-state snapshot)

A snapshot is a payload whose top-level value is `SnapshotMetadata` followed by a `WorldStateSerializer` dump, written by `SnapshotSerializer.SaveSnapshot`.

```
[Header]                          16 bytes
[BitField prelude]                variable
[SnapshotMetadata]                {Version, FixedFrame, BlobIds}
[ECS state, via WorldStateSerializer]
   [ComponentStore]
   [SetStore]
   [EntitySubmitter]
   [UniqueHeap] [SharedHeap] [NativeSharedHeap] [NativeUniqueHeap]
   [int32 WorldStateStreamGuard = 510120270]
[byte EndOfPayloadMarker = 0x5E]
```

`WorldStateStreamGuard` is a second magic integer *inside* the ECS-state section. It is not `EndOfPayloadMarker`; both exist because they catch different failure modes:

- `EndOfPayloadMarker` guards the outer stream against truncation.
- `WorldStateStreamGuard` guards the ECS-state write/read sequence against drift — if a Serialize/Deserialize pair falls out of sync (e.g. a new heap type is added on write but not read), the guard mismatches and fails loudly instead of silently reading garbage into component arrays.

## Recording bundle payload

A recording bundle is a self-contained payload that embeds an initial-state snapshot, the recorded input queue, sparse per-frame checksums, and any auto-anchors and user snapshots captured during recording. Written and read by `RecordingBundleSerializer`.

```
[Header]                          16 bytes
[BitField prelude]                variable
[BundleHeader]                    {Version, StartFixedFrame, EndFixedFrame,
                                   FixedDeltaTime, ChecksumFlags, BlobIds}
[InitialSnapshot bytes]           length-prefixed; full snapshot payload
[uint32 InitialSnapshotChecksum]
[InputQueue bytes]                length-prefixed; serialized EntityInputQueue
[Checksums]                       DenseDictionary<int, uint>
[int32 anchorCount]
[ {int32 FixedFrame, uint32 Checksum, byte[] Payload} ... ]
[int32 snapshotCount]
[ {int32 FixedFrame, uint32 Checksum, string Label, byte[] Payload} ... ]
[int32 TrecsConstants.RecordingSentinelValue = 584488256]
[byte EndOfPayloadMarker = 0x5E]
```

Each anchor and snapshot `Payload` is a full snapshot payload (header, framing, world state, end-of-payload marker) — feeding one to `SnapshotSerializer.LoadSnapshot(stream)` restores world state at that frame. Anchors are auto-placed by the recorder at `BundleRecorderSettings.AnchorIntervalSeconds` cadence and used as desync-recovery points and scrub anchors in editor tooling. Snapshots are user-placed and carry a label.

`RecordingSentinelValue` plays the same role as `WorldStateStreamGuard` but for bundles: it catches drift between writer and reader of the bundle layout.

`Checksums` is a `DenseDictionary<int, uint>` mapping fixed-frame number to a world-state checksum computed at recording time. `BundlePlayer` recomputes the checksum on the same frames and raises a desync when they disagree. The hash is FNV-1a (32-bit) — non-cryptographic and not collision-resistant, but sufficient for sanity-check desync detection: a missed collision on one checksum frame is caught by the next, since real desyncs diverge further over time.

## Endianness and portability

The format mixes two encodings:

- **Primitives routed through `BinaryWriter` / `BinaryReader`** — header fields, explicit `Write<int>` / `Write<long>` / `Write<bool>` calls, sentinels. Always little-endian (the .NET `BinaryWriter` / `BinaryReader` spec).
- **Blittable structs routed through `MemoryBlitter`** — `BlitWrite<T>`, `BlitWriteArray<T>`, `BlitWriteRawBytes`. Raw memory copy, so host-native endian.

On every platform Unity ships (x64, ARM64, WebGL) both encodings are little-endian in practice, so payloads round-trip cleanly. A hypothetical big-endian host would produce files where `BinaryWriter` output is LE while blit output is BE, making them unreadable on little-endian hosts and vice versa. Treat snapshots and bundles as non-portable across architectures; do not share them between players on different CPUs without an explicit portability test.

## Integrity

A single `0x5E` byte is the only payload-integrity check. A bit flip inside the data section is not detected; the per-frame checksums embedded in a bundle catch most such cases but are FNV-1a, which has known collision risk. For stronger integrity, wrap the stream in your own CRC / HMAC envelope before passing it to `SaveSnapshot(stream)` / `RecordingBundleSerializer.Save(stream)`.

## Forward compatibility

Trecs does **not** migrate old payloads automatically. When you change a serializer's shape:

1. Bump `version` in the `SaveSnapshot` / `StartRecording` call.
2. In your custom serializer's `Deserialize`, branch on `reader.Version` to handle historical layouts. See the example at the bottom of [Serialization](serialization.md#versioned-custom-serializers).
3. If in-serializer branching becomes unmanageable, wrap the payload in your own versioned format and run a migration pass before calling `LoadSnapshot`.

## See also

- [Serialization](serialization.md) — user-facing API.
- [Recording & Playback](recording-and-playback.md) — record/playback wrappers and determinism notes.
