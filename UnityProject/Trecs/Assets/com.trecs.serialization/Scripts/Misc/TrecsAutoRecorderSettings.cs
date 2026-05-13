namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Tunables for <see cref="TrecsAutoRecorder"/>'s capture cadence and
    /// in-memory caps. Saved per-game via <c>TrecsPlayerSettingsStore</c>
    /// (EditorPrefs-backed) and pushed onto each new recorder when its
    /// controller registers.
    /// </summary>
    public sealed class TrecsAutoRecorderSettings
    {
        /// <summary>
        /// Wall-clock seconds (in simulation time, derived from FixedDeltaTime
        /// and frame counts) between persisted-anchor captures. Anchors are
        /// the snapshots that survive Save/Load and serve as desync-recovery
        /// points during runtime playback. Sparse — larger values reduce file
        /// size; smaller values reduce the maximum amount of resimulation
        /// needed during playback recovery.
        /// </summary>
        public float AnchorIntervalSeconds = 30f;

        /// <summary>
        /// Wall-clock seconds (simulation time) between transient scrub-cache
        /// captures. The scrub cache is in-memory only — never saved — and
        /// makes recent-frame scrub-back instant. Smaller intervals = snappier
        /// scrubbing at the cost of memory.
        /// </summary>
        public float ScrubCacheIntervalSeconds = 1f;

        /// <summary>
        /// Capture a checksum every N fixed frames during live recording.
        /// Persisted into the saved bundle's <c>Checksums</c> dict so
        /// <see cref="BundlePlayer"/> can detect desyncs close to where they
        /// happen during playback (rather than only at sparse anchor frames).
        /// Smaller = catches desyncs earlier; larger = less per-frame cost.
        /// Must be &gt;= 1. Matches <see cref="BundleRecorderSettings.ChecksumFrameInterval"/>.
        /// </summary>
        public int ChecksumFrameInterval = 30;

        /// <summary>
        /// User-defined schema version stamped onto saved bundles. Trecs does
        /// not interpret this; it's surfaced as <see cref="BundleHeader.Version"/>
        /// so callers can decide whether a saved bundle is compatible with the
        /// current world schema.
        /// </summary>
        public int Version = 1;

        /// <summary>
        /// Maximum number of persisted anchors kept in memory. Oldest is
        /// dropped when the cap is hit. 0 means unbounded — fine for short
        /// debug sessions, not recommended for hours-long recordings.
        /// </summary>
        public int MaxAnchorCount = 0;

        /// <summary>
        /// Maximum total bytes of transient scrub-cache snapshots kept in
        /// memory. Oldest is dropped when the cap is hit. Default 64 MB —
        /// holds about a minute of dense scrub points for a typical world.
        /// </summary>
        public long MaxScrubCacheBytes = 64L * 1024 * 1024;
    }
}
