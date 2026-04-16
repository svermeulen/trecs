namespace Trecs
{
    /// <summary>
    /// Configuration options passed to <see cref="WorldBuilder.SetSettings"/> that control
    /// simulation timing, determinism, diagnostics, and lifecycle behavior.
    /// </summary>
    public class WorldSettings
    {
        public const float DefaultFixedTimeStep = 1.0f / 60.0f;

        public float FixedTimeStep { get; init; } = DefaultFixedTimeStep;

        public ulong? RandomSeed { get; init; }
        public float? MaxSecondsForFixedUpdatePerFrame { get; init; }

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

        public bool WarnOnFixedUpdateFallingBehind { get; init; }
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
    }
}
