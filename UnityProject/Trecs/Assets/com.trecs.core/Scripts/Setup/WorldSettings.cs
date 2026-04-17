namespace Trecs
{
    /// <summary>
    /// Configuration options passed to <see cref="WorldBuilder.SetSettings"/> that control
    /// simulation timing, determinism, diagnostics, and lifecycle behavior.
    /// </summary>
    public class WorldSettings
    {
        public const float DefaultFixedTimeStep = 1.0f / 60.0f;
        public const float DefaultMaxSecondsForFixedUpdatePerFrame = 1.0f / 3.0f;

        /// <summary>
        /// Duration of each fixed update tick in seconds.
        /// </summary>
        public float FixedTimeStep { get; init; } = DefaultFixedTimeStep;

        /// <summary>
        /// Seed for the deterministic random number generator.
        /// When null, a random seed is chosen automatically.
        /// </summary>
        public ulong? RandomSeed { get; init; }

        /// <summary>
        /// Maximum wall-clock time (in seconds) that can be spent on fixed updates
        /// in a single frame. When exceeded, the simulation skips forward to prevent
        /// the spiral of death (where falling behind causes ever-increasing catch-up work).
        /// Note that skipping forward means fixed update ticks are dropped, which breaks
        /// determinism and may cause desyncs with recordings or networked peers.
        /// Defaults to 1/3s, matching Unity's <c>Time.maximumDeltaTime</c>.
        /// Set to null for unlimited updates (e.g. deterministic replay), but note
        /// this risks the spiral of death if fixed updates are too expensive.
        /// </summary>
        public float? MaxSecondsForFixedUpdatePerFrame { get; init; } =
            DefaultMaxSecondsForFixedUpdatePerFrame;

        /// <summary>
        /// When true, the world starts in a paused state and must be explicitly unpaused.
        /// </summary>
        public bool StartPaused { get; init; }

        /// <summary>
        /// When false, you must call World.TriggerAllRemoveEvents manually.
        /// Useful when you need to run logic after all entities are removed
        /// but before WorldAccessor is disposed.
        /// </summary>
        public bool TriggerRemoveEventsOnDispose { get; init; } = true;

        /// <summary>
        /// When true, native operations (adds, removes, moves) are sorted before processing
        /// to ensure fully deterministic submission order regardless of thread scheduling.
        /// Required for deterministic replay/recording. When false, native operations are
        /// processed in bag order (per-thread FIFO), which is faster but not deterministic
        /// across runs with different thread counts or scheduling.
        /// </summary>
        public bool RequireDeterministicSubmission { get; init; }

        /// <summary>
        /// When true, logs a warning if fixed updates fall behind and the simulation
        /// has to skip forward (when <see cref="MaxSecondsForFixedUpdatePerFrame"/> is set)
        /// or is at risk of entering the spiral of death (when it is null).
        /// </summary>
        public bool WarnOnFixedUpdateFallingBehind { get; init; } = true;

        /// <summary>
        /// When true, logs a warning whenever a job sync point is hit
        /// (i.e. the main thread blocks waiting for jobs to complete).
        /// </summary>
        public bool WarnOnJobSyncPoints { get; init; }

        /// <summary>
        /// When true, logs a warning on dispose for any template that was registered
        /// but never had entities created in any of its groups.
        /// </summary>
        public bool WarnOnUnusedTemplates { get; init; }

        /// <summary>
        /// Maximum number of submission iterations before throwing a circular submission error.
        /// Each iteration processes structural changes that may trigger further changes via callbacks.
        /// </summary>
        public int MaxSubmissionIterations { get; init; } = 10;

        /// <summary>
        /// When true, accessing <see cref="WorldAccessor.DeltaTime"/>, <see cref="WorldAccessor.ElapsedTime"/>,
        /// <see cref="WorldAccessor.FixedDeltaTime"/>, or <see cref="WorldAccessor.FixedElapsedTime"/> during
        /// the fixed-update phase throws. In Burst jobs (where exceptions are unavailable),
        /// <see cref="NativeWorldAccessor.DeltaTime"/> and <see cref="NativeWorldAccessor.ElapsedTime"/> are
        /// populated with <see cref="float.NaN"/> instead, so any arithmetic that reads them produces
        /// visibly broken output.
        ///
        /// <para>
        /// Enable this for deterministic-lockstep workloads (e.g. RTS networking) where the simulation must
        /// produce bit-identical results across machines. Trecs itself guarantees deterministic scheduling,
        /// iteration, and entity ordering, but it cannot guarantee deterministic floating-point math:
        /// </para>
        ///
        /// <para>
        /// Use <see cref="WorldAccessor.FixedFrame"/> as a discrete tick counter instead.
        /// </para>
        /// </summary>
        public bool AssertNoTimeInFixedPhase { get; init; }
    }
}
