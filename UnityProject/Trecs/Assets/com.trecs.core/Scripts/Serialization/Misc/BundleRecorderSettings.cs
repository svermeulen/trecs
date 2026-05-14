namespace Trecs.Internal
{
    /// <summary>
    /// Tunables for <see cref="BundleRecorder"/>'s capture cadence. Sparse
    /// auto-anchors give recovery points across long recordings; dense per-
    /// frame checksums catch desyncs close to where they happen during
    /// playback.
    /// </summary>
    public sealed class BundleRecorderSettings
    {
        /// <summary>
        /// User-defined schema version stored on the produced bundle.
        /// </summary>
        public int Version = 1;

        /// <summary>
        /// Wall-clock seconds (in simulation time, derived from FixedDeltaTime
        /// and frame counts) between auto-placed anchor snapshots. Anchors
        /// double as scrub points in the editor and as desync-recovery
        /// points during runtime playback. Larger = smaller files; smaller =
        /// faster recovery.
        /// </summary>
        public float AnchorIntervalSeconds = 30f;

        /// <summary>
        /// Capture a checksum every N fixed frames during recording. Smaller
        /// = catches desyncs closer to where they happen; larger = less
        /// per-frame cost. Must be &gt;= 1.
        /// </summary>
        public int ChecksumFrameInterval = 30;

        /// <summary>
        /// Serialization flags passed to the checksum serializer. Required
        /// when any user serializer branches on writer flags (e.g. to exclude
        /// non-deterministic state from checksums) — playback recomputes
        /// with the same flags via the bundle header.
        /// </summary>
        public long ChecksumFlags = SerializationFlags.IsForChecksum;
    }
}
