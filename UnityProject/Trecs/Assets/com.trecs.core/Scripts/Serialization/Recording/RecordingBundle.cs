using System;
using System.Collections.Generic;
using Trecs.Collections;
using Trecs.Serialization;

namespace Trecs.Internal
{
    /// <summary>
    /// A self-contained replayable session bundled into a single file: an
    /// initial world-state snapshot, the input queue covering the recorded
    /// frame range, sparse desync-detection checksums, optional auto-keyframe
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
        /// <see cref="Keyframes"/>.
        /// </summary>
        public IterableDictionary<int, ulong> Checksums { get; init; }

        /// <summary>
        /// Auto-placed full-state snapshots used as desync-recovery points
        /// during runtime playback and as scrub-back keyframes in the editor.
        /// Sparse and ordered by frame. All entries have
        /// <see cref="WorldSnapshot.Kind"/> equal to
        /// <see cref="SnapshotKind.Keyframe"/>; their
        /// <see cref="WorldSnapshot.Label"/> is empty.
        /// </summary>
        public IReadOnlyList<WorldSnapshot> Keyframes { get; init; }

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
        /// User-defined version. Trecs does not interpret this. Structural
        /// schema compatibility is checked automatically via
        /// <see cref="SchemaFingerprint"/>; this field is for versioning the
        /// things the fingerprint deliberately does not cover — e.g. the
        /// payload format of custom <see cref="Trecs.ISerializer{T}"/>
        /// implementations (branch on <c>reader.Version</c>) or
        /// application-level save semantics.
        /// </summary>
        public int Version { get; init; }

        public int StartFixedFrame { get; init; }
        public int EndFixedFrame { get; init; }

        /// <summary>
        /// Fixed-update delta time the bundle was recorded with. Input replay
        /// against a different tick rate desyncs, so
        /// <c>BundleReplayer.Start</c> logs a warning when the live runner's
        /// rate disagrees with this value.
        /// </summary>
        public float FixedDeltaTime { get; init; }

        /// <summary>Heap blob references the bundle's snapshots depend on.</summary>
        public IterableHashSet<BlobId> BlobIds { get; init; } = new();

        /// <summary>
        /// Fingerprint of the world schema the recording was captured
        /// against (see <see cref="WorldSchemaFingerprint"/>). Validated by
        /// <c>BundleReplayer.Start</c> before any world mutation; a mismatch
        /// throws a <see cref="SerializationException"/> explaining which
        /// aspect of the schema diverged. Readable without parsing the full
        /// bundle via <see cref="RecordingBundleSerializer.PeekHeader(System.IO.Stream)"/>.
        /// </summary>
        public WorldSchemaFingerprint SchemaFingerprint { get; init; }

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            // Composite serializers used inside the bundle's payload — the
            // checksum dict and the blob-id set on the header. Registered
            // here rather than in TrecsSerialization's general-purpose set
            // so the bundle is self-contained for users who only need it.
            registry.RegisterSerializer<IterableDictionarySerializerUnmanaged<int, ulong>>();
            registry.RegisterSerializer<IterableHashSetSerializer<BlobId>>();
            registry.RegisterSerializer(new Serializer());
        }

        internal sealed class Serializer : ISerializer<BundleHeader>
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
                var schemaFingerprint = reader.Read<WorldSchemaFingerprint>("SchemaFingerprint");

                value = new BundleHeader
                {
                    BundleFormatVersion = bundleFormatVersion,
                    Version = version,
                    StartFixedFrame = startFrame,
                    EndFixedFrame = endFrame,
                    FixedDeltaTime = fixedDeltaTime,
                    BlobIds = blobIds,
                    SchemaFingerprint = schemaFingerprint,
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
                writer.Write("SchemaFingerprint", value.SchemaFingerprint);
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
        /// <c>KeyframeIntervalSeconds</c> cadence (plus optional manual
        /// captures via <c>CaptureKeyframeAtCurrentFrame</c>). Used as a
        /// desync-recovery point during runtime playback and a scrub-back
        /// point in the editor. Subject to the recorder's max-keyframe cap
        /// (drop-oldest when full); the <see cref="WorldSnapshot.Label"/>
        /// is always the empty string.
        /// </summary>
        Keyframe = 0,

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
    /// type carries both auto-cadenced keyframes (recovery / scrub points)
    /// and user-labelled bookmarks; the two are distinguished by
    /// <see cref="Kind"/>.
    /// </summary>
    public sealed class WorldSnapshot
    {
        /// <summary>Fixed-update frame this snapshot was captured at.</summary>
        public int FixedFrame { get; init; }

        /// <summary>
        /// Categorizes this entry — auto-cadenced keyframe or user-labelled
        /// bookmark. Drives policy (cap enforcement, trim survival) and
        /// UI rendering; the on-disk payload is identical between the two.
        /// </summary>
        public SnapshotKind Kind { get; init; }

        /// <summary>
        /// Display label for the recorder UI. Required to be non-null;
        /// the empty string for <see cref="SnapshotKind.Keyframe"/> entries.
        /// </summary>
        public string Label { get; init; } = "";

        /// <summary>
        /// Retained two-section payload, used by the live capture/retain path so a snapshot is
        /// the serializer's own output (no contiguous copy). Mutually exclusive with
        /// <see cref="Payload"/>: when set, <see cref="Payload"/> is empty and the holding store
        /// owns this instance, despawning it to a <see cref="SerializationDataPool"/> on removal.
        /// Null for snapshots loaded from a bundle (those use <see cref="Payload"/>).
        /// </summary>
        public SerializationData RetainedData { get; init; }

        /// <summary>
        /// Snapshot payload bytes — a self-contained
        /// <see cref="SnapshotSerializer"/> payload. The length of this
        /// memory is the authoritative byte count; in-memory buffers may
        /// be backed by oversized pooled arrays, but the
        /// <see cref="ReadOnlyMemory{T}"/> slice is always exact and
        /// safe to read in full. Empty when <see cref="RetainedData"/> is set.
        /// </summary>
        public ReadOnlyMemory<byte> Payload { get; init; }

        /// <summary>
        /// Cache pins keeping this snapshot's opaque (eager) blobs resident while it lives in
        /// memory — the live-capture path's replacement for capture-time disk write-through. Each
        /// pin must be disposed (via the <see cref="BlobCache"/>) when the snapshot is dropped:
        /// <c>SnapshotStore</c> does this through its pin-release callback, and
        /// <c>TrecsRewindBuffer</c> does it for the desync snapshot it owns directly. Null/empty for
        /// snapshots loaded from a bundle (those resolve their blobs from the
        /// <see cref="IOpaqueBlobStore"/> on demand instead) and for the transient contiguous copies
        /// produced at save time. The concrete <see cref="List{T}"/> type is deliberate: the
        /// release path returns the (cleared) list to <c>TrecsRewindBuffer</c>'s pool, so the
        /// capture cadence allocates no steady-state garbage.
        /// </summary>
        internal List<BlobPin> PinnedBlobs { get; init; }

        /// <summary>
        /// Approximate resident byte footprint, for capacity accounting. For a
        /// <see cref="RetainedData"/>-backed snapshot this is the two section lengths; otherwise
        /// the contiguous <see cref="Payload"/> length.
        /// </summary>
        public int PayloadByteSize =>
            RetainedData != null
                ? RetainedData.BitFieldBytes.Length + RetainedData.Data.Length
                : Payload.Length;
    }
}
