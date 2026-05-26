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
    }
}
