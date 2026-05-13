using System.Collections.Generic;
using Trecs.Collections;

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// A self-contained replayable session bundled into a single file: an
    /// initial world-state snapshot, the input queue covering the recorded
    /// frame range, sparse desync-detection checksums, optional auto-anchor
    /// snapshots used as desync-recovery / scrub-back points, and optional
    /// user snapshots placed by the user (e.g. just before a bug)
    /// for navigation in the recorder UI.
    ///
    /// Use <see cref="RecordingBundleSerializer"/> to read or write a bundle
    /// to/from a stream or file.
    /// </summary>
    public sealed class RecordingBundle
    {
        public BundleHeader Header { get; init; }

        /// <summary>
        /// SnapshotSerializer payload bytes for the world state at
        /// <see cref="BundleHeader.StartFixedFrame"/>. Required: a bundle
        /// without an initial snapshot cannot be replayed.
        /// </summary>
        public byte[] InitialSnapshot { get; init; }

        /// <summary>
        /// World-state checksum captured at the same time as
        /// <see cref="InitialSnapshot"/>. Used to verify the snapshot
        /// deserializes back to identical state, and to re-verify on
        /// scrub-back when the simulation re-runs from the initial frame.
        /// </summary>
        public uint InitialSnapshotChecksum { get; init; }

        /// <summary>
        /// EntityInputQueue payload bytes covering the recorded frame range.
        /// May be empty (no inputs captured) but must not be null.
        /// </summary>
        public byte[] InputQueue { get; init; }

        /// <summary>
        /// Sparse per-frame world-state checksums for desync detection during
        /// playback. Cadence is independent of <see cref="Anchors"/>.
        /// </summary>
        public DenseDictionary<int, uint> Checksums { get; init; }

        /// <summary>
        /// Auto-placed full-state snapshots used as desync-recovery points
        /// during runtime playback and as scrub-back anchors in the editor.
        /// Sparse and ordered by frame.
        /// </summary>
        public IReadOnlyList<BundleAnchor> Anchors { get; init; }

        /// <summary>
        /// User-placed full-state snapshots with labels, surfaced in the
        /// recorder UI for navigation. Ordered by frame.
        /// </summary>
        public IReadOnlyList<BundleSnapshot> Snapshots { get; init; }
    }

    /// <summary>
    /// Frame-range and schema metadata for a <see cref="RecordingBundle"/>.
    /// Cheap to read standalone via
    /// <see cref="RecordingBundleSerializer.PeekHeader(System.IO.Stream)"/>
    /// — all variable-size payloads (snapshots, inputs, checksums) follow
    /// the header in the wire format.
    /// </summary>
    [TypeId(423917628)]
    public sealed class BundleHeader
    {
        /// <summary>
        /// User-defined schema version. Trecs does not interpret this; it is
        /// surfaced so callers can decide whether a bundle is compatible
        /// with the current world schema.
        /// </summary>
        public int Version { get; init; }

        public int StartFixedFrame { get; init; }
        public int EndFixedFrame { get; init; }

        /// <summary>
        /// Fixed-update delta time the bundle was recorded with. Loaders can
        /// surface a warning when the live runner's tick rate disagrees,
        /// since input replay against a different tick rate desyncs.
        /// </summary>
        public float FixedDeltaTime { get; init; }

        /// <summary>
        /// Serialization flags used when computing per-frame checksums during
        /// recording. Playback must recompute checksums with the same flags.
        /// </summary>
        public long ChecksumFlags { get; init; }

        /// <summary>Heap blob references the bundle's snapshots depend on.</summary>
        public DenseHashSet<BlobId> BlobIds { get; init; } = new();

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            // Composite serializers used inside the bundle's payload — the
            // checksum dict and the blob-id set on the header. Registered
            // here rather than in TrecsSerialization's general-purpose set
            // so the bundle is self-contained for users who only need it.
            registry.RegisterSerializer<DenseDictionarySerializer<int, uint>>();
            registry.RegisterSerializer<DenseHashSetSerializer<BlobId>>();
            registry.RegisterSerializer<Serializer>();
        }

        public sealed class Serializer : ISerializer<BundleHeader>
        {
            public Serializer() { }

            public void Deserialize(ref BundleHeader value, ISerializationReader reader)
            {
                var version = reader.Read<int>("Version");
                var startFrame = reader.Read<int>("StartFixedFrame");
                var endFrame = reader.Read<int>("EndFixedFrame");
                var fixedDeltaTime = reader.Read<float>("FixedDeltaTime");
                var checksumFlags = reader.Read<long>("ChecksumFlags");
                var blobIds = reader.Read<DenseHashSet<BlobId>>("BlobIds");

                value = new BundleHeader
                {
                    Version = version,
                    StartFixedFrame = startFrame,
                    EndFixedFrame = endFrame,
                    FixedDeltaTime = fixedDeltaTime,
                    ChecksumFlags = checksumFlags,
                    BlobIds = blobIds,
                };
            }

            public void Serialize(in BundleHeader value, ISerializationWriter writer)
            {
                writer.Write("Version", value.Version);
                writer.Write("StartFixedFrame", value.StartFixedFrame);
                writer.Write("EndFixedFrame", value.EndFixedFrame);
                writer.Write("FixedDeltaTime", value.FixedDeltaTime);
                writer.Write("ChecksumFlags", value.ChecksumFlags);
                writer.Write("BlobIds", value.BlobIds);
            }
        }
    }

    /// <summary>
    /// Auto-placed full-state snapshot. Carries no label; rendered as a
    /// faint marker in the recorder UI's timeline. Doubles as a runtime
    /// desync-recovery point on the playback side.
    /// </summary>
    public sealed class BundleAnchor
    {
        public int FixedFrame { get; init; }

        /// <summary>
        /// World-state checksum at capture time (computed with
        /// <see cref="BundleHeader.ChecksumFlags"/>). Used to verify the
        /// snapshot deserialized cleanly before the simulation resumes from it.
        /// </summary>
        public uint Checksum { get; init; }

        /// <summary>SnapshotSerializer payload bytes for this anchor's world state.</summary>
        public byte[] Payload { get; init; }
    }

    /// <summary>
    /// User-placed full-state snapshot. Carries a label string for display
    /// in the recorder UI's timeline. Snapshots are independent of
    /// <see cref="BundleAnchor"/>s; deleting a snapshot never removes an
    /// auto-anchor that happens to share a frame.
    /// </summary>
    public sealed class BundleSnapshot
    {
        public int FixedFrame { get; init; }
        public uint Checksum { get; init; }

        /// <summary>
        /// Caller-supplied label, displayed in the recorder UI. Must not be
        /// null (use the empty string for an unlabeled snapshot).
        /// </summary>
        public string Label { get; init; }

        /// <summary>SnapshotSerializer payload bytes for this snapshot's world state.</summary>
        public byte[] Payload { get; init; }
    }
}
