namespace Trecs.Serialization
{
    /// <summary>
    /// Action taken when the recorder hits its memory or count cap.
    /// </summary>
    public enum CapacityOverflowAction
    {
        /// <summary>
        /// Drop the oldest snapshots to make room (rolling buffer). Useful
        /// when only the recent past matters.
        /// </summary>
        DropOldest,

        /// <summary>
        /// Pause the SystemRunner's fixed phase so capture stops growing.
        /// The user can then save/fork/reset before resuming. Default: chosen
        /// for safety so a forgotten window doesn't burn through RAM.
        /// </summary>
        Pause,
    }

    public class TrecsAutoRecorderSettings
    {
        /// <summary>
        /// Wall-clock seconds (in simulation time, derived from FixedDeltaTime
        /// and frame counts) between snapshot snapshots. Smaller values give
        /// faster scrubbing at the cost of memory; larger values save memory
        /// at the cost of more resimulation when jumping. Per-game tuning
        /// expected.
        /// </summary>
        public float SnapshotIntervalSeconds = 0.5f;

        public int Version = 1;

        /// <summary>
        /// Maximum number of snapshots kept in memory before
        /// <see cref="OverflowAction"/> kicks in. 0 means unbounded
        /// (not recommended outside short-lived diagnostic sessions).
        /// </summary>
        public int MaxSnapshotCount = 0;

        /// <summary>
        /// Maximum total bytes of snapshot data kept in memory before
        /// <see cref="OverflowAction"/> kicks in. 0 means unbounded.
        /// Default: 256 MB — chosen as a reasonable safety net for
        /// editor-side debugging on developer machines.
        /// </summary>
        public long MaxSnapshotMemoryBytes = 256L * 1024 * 1024;

        /// <summary>
        /// What to do when a cap is reached. Defaults to
        /// <see cref="CapacityOverflowAction.Pause"/> so an accidentally-left-on
        /// recording cannot grow without bound.
        /// </summary>
        public CapacityOverflowAction OverflowAction = CapacityOverflowAction.DropOldest;
    }
}
