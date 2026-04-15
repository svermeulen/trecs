using System;

namespace Trecs
{
    [Serializable]
    public class WorldSettings
    {
        // I think this was used because it is the default in unity ecs
        public const float DefaultFixedTimeStep = 1.0f / 60.0f;

        public float FixedTimeStep = DefaultFixedTimeStep;

        public ulong? RandomSeed;
        public float? MaxSecondsForFixedUpdatePerFrame;

        public bool StartPaused;

        // It can be useful to set this to false then call World.TriggerAllRemoveEvents manually
        // This is useful because you might want to run logic after all entities are removed,
        // but before WorldAccessor is disposed
        public bool TriggerRemoveEventsOnDispose = true;

        /// <summary>
        /// When true, native operations (adds, removes, moves) are sorted before processing
        /// to ensure fully deterministic submission order regardless of thread scheduling.
        /// Required for deterministic replay/recording. When false, native operations are
        /// processed in bag order (per-thread FIFO), which is faster but not deterministic
        /// across runs with different thread counts or scheduling.
        /// </summary>
        public bool RequireDeterministicSubmission = false;

        public bool WarnOnFixedUpdateFallingBehind = false;
        public bool WarnOnJobSyncPoints = false;

        /// <summary>
        /// When true, logs a warning on dispose for any template that was registered
        /// but never had entities created in any of its groups.
        /// </summary>
        public bool WarnOnUnusedTemplates = false;

        /// <summary>
        /// When true (in DEBUG builds without TRECS_IS_PROFILING), logs a warning at submission time
        /// for any entity that was created via AddEntity without calling AssertComplete(),
        /// and has required components that were not initialized via Set.
        /// </summary>
        public bool WarnOnMissingAssertComplete = false;

        /// <summary>
        /// Maximum number of submission iterations before throwing a circular submission error.
        /// Each iteration processes structural changes that may trigger further changes via callbacks.
        /// </summary>
        public int MaxSubmissionIterations = 10;
    }
}
