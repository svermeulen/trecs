namespace Trecs.Serialization
{
    /// <summary>
    /// Result from a single <see cref="BundlePlayer.Tick"/> call. When the
    /// current tick had a recorded checksum, both <see cref="ExpectedChecksum"/>
    /// and <see cref="ActualChecksum"/> are populated and <see cref="ChecksumVerified"/>
    /// is true; otherwise both are null and the other flags are false.
    /// </summary>
    public struct PlaybackTickResult
    {
        /// <summary>
        /// Checksum that the recording expected at this frame. Null when this
        /// tick had no recorded checksum (most frames — checksums are sampled
        /// at a configured interval when the recording was made).
        /// </summary>
        public uint? ExpectedChecksum { get; init; }

        /// <summary>
        /// Checksum calculated from the live world after this tick. Null when
        /// this tick had no recorded checksum.
        /// </summary>
        public uint? ActualChecksum { get; init; }

        /// <summary>
        /// True when this tick had a recorded checksum and was therefore
        /// compared against the live world.
        /// </summary>
        public bool ChecksumVerified => ExpectedChecksum.HasValue;

        /// <summary>
        /// True when a checksum was verified and did not match — i.e., the
        /// replay has diverged from the recorded simulation.
        /// </summary>
        public bool DesyncDetected =>
            ExpectedChecksum.HasValue && ExpectedChecksum != ActualChecksum;
    }
}
