namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Source axis of <see cref="TrecsGameStateController"/>: where the
    /// world's frame data is coming from / going to. Orthogonal to the
    /// SystemRunner's pause/play state.
    /// </summary>
    public enum GameStateMode
    {
        /// <summary>No recording session active. Slider is inert.</summary>
        Idle,

        /// <summary>
        /// Recorder running and the world is at the live edge of its buffer
        /// (no pending divergence). Snapshots are being captured as time
        /// advances.
        /// </summary>
        Recording,

        /// <summary>
        /// Recorder running but the world has been scrubbed back into the
        /// buffer. Snapshots past the scrub point are tentatively preserved
        /// and only get truncated when the simulation actually advances past
        /// the divergence with live input.
        /// </summary>
        Playback,
    }
}
