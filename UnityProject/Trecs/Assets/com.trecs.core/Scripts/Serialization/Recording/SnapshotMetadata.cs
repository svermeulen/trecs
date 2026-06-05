using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Header data for a snapshot. Populated by
    /// <see cref="SnapshotSerializer.Deserialize"/>, and readable without
    /// restoring full state via <see cref="SnapshotSerializer.PeekMetadata"/>.
    /// </summary>
    [TypeId(136305329)]
    public sealed class SnapshotMetadata
    {
        /// <summary>
        /// User-defined version written at save time. Trecs does not
        /// interpret this value. Structural schema compatibility is checked
        /// automatically via <see cref="SchemaFingerprint"/>; this field is
        /// for versioning the things the fingerprint deliberately does not
        /// cover — e.g. the payload format of custom
        /// <see cref="Trecs.ISerializer{T}"/> implementations (branch on
        /// <c>reader.Version</c>) or application-level save semantics.
        /// </summary>
        // Plain {get; set;} (not init) so SnapshotSerializer can reuse a
        // single instance across SaveSnapshot calls — avoiding a per-snapshot
        // heap allocation on the recorder's hot path.
        public int Version { get; set; }

        /// <summary>World fixed-frame at capture time.</summary>
        public int FixedFrame { get; set; }

        /// <summary>
        /// Fingerprint of the world schema the snapshot was saved against
        /// (see <see cref="WorldSchemaFingerprint"/>). Validated by
        /// <see cref="SnapshotSerializer"/> before any world state is read;
        /// a mismatch throws a <see cref="SerializationException"/> that
        /// explains which aspect of the schema diverged. Readable without a
        /// full load via <see cref="SnapshotSerializer.PeekMetadata"/>.
        /// </summary>
        public WorldSchemaFingerprint SchemaFingerprint { get; set; }

        /// <summary>
        /// Heap blob references the snapshot depends on. Owned by this
        /// instance and populated in place (cleared + refilled) by the
        /// serializer / <see cref="SnapshotSerializer"/> — never replaced.
        /// </summary>
        public IterableHashSet<BlobId> BlobIds { get; } = new();

        // Runtime-only stamp (never serialized) backing SnapshotSerializer.PrepareMetadata's
        // rebuild-skip: records which world's heaps BlobIds was last collected from and the two
        // shared heaps' blob-membership versions at that moment. While the stamp still matches,
        // BlobIds is exactly what a fresh collection would produce (same membership, same
        // insertion order) and the per-blob rebuild is skipped — the steady-state rollback case.
        // A null world means no valid stamp. Invalidated when BlobIds is repopulated from the
        // wire; re-stamped by SnapshotSerializer.Deserialize after a full-state load (post-load
        // heaps hold exactly this snapshot's blobs, in this snapshot's order). Mutating BlobIds
        // by hand without clearing the stamp would corrupt subsequent saves' wire form — DEBUG
        // builds verify every skipped rebuild against a fresh collection.
        internal World RuntimeBlobIdsStampWorld;
        internal long RuntimeBlobIdsSharedVersion;
        internal long RuntimeBlobIdsNativeVersion;

        public static void RegisterSerializers(SerializerRegistry registry)
        {
            registry.RegisterSerializer(new Serializer());
        }

        internal sealed class Serializer : ISerializer<SnapshotMetadata>
        {
            public Serializer() { }

            public void Deserialize(ref SnapshotMetadata value, ISerializationReader reader)
            {
                // Populate in place when the caller supplies an instance (the
                // ReadInPlace contract) so deserialization can reuse a
                // caller-owned metadata object instead of allocating one.
                value ??= new SnapshotMetadata();
                value.Version = reader.Read<int>("Version");
                value.BlobIds.Clear();
                using (TrecsProfiling.Start("Reading metadata blob ids"))
                {
                    reader.ReadInPlace("BlobIds", value.BlobIds);
                }
                // BlobIds no longer reflects a live-heap collection — see the stamp fields.
                value.RuntimeBlobIdsStampWorld = null;
                value.FixedFrame = reader.Read<int>("FixedFrame");
                value.SchemaFingerprint = reader.Read<WorldSchemaFingerprint>("SchemaFingerprint");
            }

            public void Serialize(in SnapshotMetadata value, ISerializationWriter writer)
            {
                writer.Write("Version", value.Version);
                using (TrecsProfiling.Start("Writing metadata blob ids"))
                {
                    writer.Write("BlobIds", value.BlobIds);
                }
                writer.Write("FixedFrame", value.FixedFrame);
                writer.Write("SchemaFingerprint", value.SchemaFingerprint);
            }
        }
    }
}
