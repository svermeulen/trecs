using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Serialization;

namespace Trecs.Internal
{
    /// <summary>
    /// A self-contained replayable session bundled into a single file: an
    /// initial world-state snapshot, the input queue covering the recorded
    /// frame range, sparse desync-detection checksums, optional auto-anchor
    /// snapshots used as desync-recovery / scrub-back points, and optional
    /// user snapshots placed by the user (e.g. just before a bug) for
    /// navigation in the recorder UI.
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
        /// without an initial snapshot cannot be replayed. Length is the
        /// exact payload size (never oversized).
        /// </summary>
        public ReadOnlyMemory<byte> InitialSnapshot { get; init; }

        /// <summary>
        /// EntityInputQueue payload bytes covering the recorded frame range.
        /// May be empty (no inputs captured) but must not be null.
        /// </summary>
        public ReadOnlyMemory<byte> InputQueue { get; init; }

        /// <summary>
        /// Sparse per-frame 64-bit xxHash world-state checksums for desync
        /// detection during playback. Cadence is independent of
        /// <see cref="Anchors"/>.
        /// </summary>
        public IterableDictionary<int, ulong> Checksums { get; init; }

        /// <summary>
        /// Auto-placed full-state snapshots used as desync-recovery points
        /// during runtime playback and as scrub-back anchors in the editor.
        /// Sparse and ordered by frame. All entries have
        /// <see cref="WorldSnapshot.Kind"/> equal to
        /// <see cref="SnapshotKind.Anchor"/>; their
        /// <see cref="WorldSnapshot.Label"/> is empty.
        /// </summary>
        public IReadOnlyList<WorldSnapshot> Anchors { get; init; }

        /// <summary>
        /// User-placed labelled bookmarks, surfaced in the recorder UI for
        /// navigation. Ordered by frame. All entries have
        /// <see cref="WorldSnapshot.Kind"/> equal to
        /// <see cref="SnapshotKind.Bookmark"/>.
        /// </summary>
        public IReadOnlyList<WorldSnapshot> Bookmarks { get; init; }
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
        /// Bundle wire-format version. Bumped whenever the bundle's binary
        /// layout changes incompatibly (new fields, reordering, type
        /// changes). Distinct from <see cref="Version"/> (user-defined
        /// schema version) and from the Layer-1 SerializationHeader format
        /// version (which guards the magic-byte envelope shared by all Trecs
        /// payloads). Defaults to <see cref="TrecsConstants.CurrentBundleFormatVersion"/>;
        /// load-time mismatch raises a <see cref="SerializationException"/>.
        /// </summary>
        public byte BundleFormatVersion { get; init; } = TrecsConstants.CurrentBundleFormatVersion;

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

        /// <summary>Heap blob references the bundle's snapshots depend on.</summary>
        public IterableHashSet<BlobId> BlobIds { get; init; } = new();

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            // Composite serializers used inside the bundle's payload — the
            // checksum dict and the blob-id set on the header. Registered
            // here rather than in TrecsSerialization's general-purpose set
            // so the bundle is self-contained for users who only need it.
            registry.RegisterSerializer<IterableDictionaryUnmanagedSerializer<int, ulong>>();
            registry.RegisterSerializer<IterableHashSetSerializer<BlobId>>();
            registry.RegisterSerializer(new Serializer());
        }

        public sealed class Serializer : ISerializer<BundleHeader>
        {
            public Serializer() { }

            public void Deserialize(ref BundleHeader value, ISerializationReader reader)
            {
                byte bundleFormatVersion = 0;
                reader.BlitRead("BundleFormatVersion", ref bundleFormatVersion);
                if (bundleFormatVersion != TrecsConstants.CurrentBundleFormatVersion)
                {
                    throw new SerializationException(
                        $"Bundle format version {bundleFormatVersion} is not supported — "
                            + $"this build expects version {TrecsConstants.CurrentBundleFormatVersion}. "
                            + "Bundles from a different Trecs build cannot be loaded here."
                    );
                }
                var version = reader.Read<int>("Version");
                var startFrame = reader.Read<int>("StartFixedFrame");
                var endFrame = reader.Read<int>("EndFixedFrame");
                var fixedDeltaTime = reader.Read<float>("FixedDeltaTime");
                var blobIds = reader.Read<IterableHashSet<BlobId>>("BlobIds");

                value = new BundleHeader
                {
                    BundleFormatVersion = bundleFormatVersion,
                    Version = version,
                    StartFixedFrame = startFrame,
                    EndFixedFrame = endFrame,
                    FixedDeltaTime = fixedDeltaTime,
                    BlobIds = blobIds,
                };
            }

            public void Serialize(in BundleHeader value, ISerializationWriter writer)
            {
                writer.BlitWrite("BundleFormatVersion", value.BundleFormatVersion);
                writer.Write("Version", value.Version);
                writer.Write("StartFixedFrame", value.StartFixedFrame);
                writer.Write("EndFixedFrame", value.EndFixedFrame);
                writer.Write("FixedDeltaTime", value.FixedDeltaTime);
                writer.Write("BlobIds", value.BlobIds);
            }
        }
    }

    /// <summary>
    /// Categorizes a <see cref="WorldSnapshot"/> by what produced it. Drives
    /// policy differences (cap enforcement, trim survival, UI rendering)
    /// without changing the payload format.
    /// </summary>
    public enum SnapshotKind : byte
    {
        /// <summary>
        /// Auto-cadenced snapshot. Captured at the recorder's
        /// <c>AnchorIntervalSeconds</c> cadence (plus optional manual
        /// captures via <c>CaptureAnchorAtCurrentFrame</c>). Used as a
        /// desync-recovery point during runtime playback and a scrub-back
        /// point in the editor. Subject to the recorder's max-anchor cap
        /// (drop-oldest when full); the <see cref="WorldSnapshot.Label"/>
        /// is always the empty string.
        /// </summary>
        Anchor = 0,

        /// <summary>
        /// User-placed labelled bookmark, surfaced in the recorder UI for
        /// navigation. Survives trims and capacity caps;
        /// <see cref="WorldSnapshot.Label"/> is the user-supplied display
        /// string (may be empty but is never null).
        /// </summary>
        Bookmark = 1,
    }

    /// <summary>
    /// Full-state snapshot inside a <see cref="RecordingBundle"/>. The same
    /// type carries both auto-cadenced anchors (recovery / scrub points)
    /// and user-labelled bookmarks; the two are distinguished by
    /// <see cref="Kind"/>.
    /// </summary>
    public sealed class WorldSnapshot
    {
        /// <summary>Fixed-update frame this snapshot was captured at.</summary>
        public int FixedFrame { get; init; }

        /// <summary>
        /// Categorizes this entry — auto-cadenced anchor or user-labelled
        /// bookmark. Drives policy (cap enforcement, trim survival) and
        /// UI rendering; the on-disk payload is identical between the two.
        /// </summary>
        public SnapshotKind Kind { get; init; }

        /// <summary>
        /// Display label for the recorder UI. Required to be non-null;
        /// the empty string for <see cref="SnapshotKind.Anchor"/> entries.
        /// </summary>
        public string Label { get; init; } = "";

        /// <summary>
        /// Snapshot payload bytes — a self-contained
        /// <see cref="SnapshotSerializer"/> payload. The length of this
        /// memory is the authoritative byte count; in-memory buffers may
        /// be backed by oversized pooled arrays, but the
        /// <see cref="ReadOnlyMemory{T}"/> slice is always exact and
        /// safe to read in full.
        /// </summary>
        public ReadOnlyMemory<byte> Payload { get; init; }
    }
}
