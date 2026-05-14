using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// The execution phase a system runs in. Phases run in the order they appear here:
    /// <list type="bullet">
    /// <item><c>Input</c> — runs just-in-time before each fixed step (0..N times per rendered frame). Deterministic. Only phase allowed to call <see cref="WorldAccessor.AddInput{T}"/>.</item>
    /// <item><c>Fixed</c> — deterministic simulation step (0..N times per rendered frame). Default when no <see cref="ExecuteInAttribute"/> is specified.</item>
    /// <item><c>EarlyPresentation</c> — runs once per rendered frame, before the fixed-update loop. Variable cadence; for sampling Unity-side state that needs to feed into fixed-update.</item>
    /// <item><c>Presentation</c> — runs once per rendered frame, after the fixed-update loop. Variable cadence; for rendering, transform sync, interpolation reads.</item>
    /// <item><c>LatePresentation</c> — runs once per rendered frame in Unity's <c>LateUpdate</c>. Variable cadence; for post-animation corrections.</item>
    /// </list>
    /// </summary>
    public enum SystemPhase
    {
        Input,
        Fixed,
        EarlyPresentation,
        Presentation,
        LatePresentation,
    }

    /// <summary>
    /// Runtime descriptor for a registered system, holding its <see cref="ISystem"/> instance,
    /// dependency graph edges, <see cref="SystemPhase"/>, and associated <see cref="WorldAccessor"/>.
    /// </summary>
    public sealed class SystemMetadata
    {
        public SystemMetadata(
            ISystem system,
            IReadOnlyCollection<int> systemDependencies,
            SystemPhase phase,
            WorldAccessor accessor,
            string debugName,
            int? executionPriority
        )
        {
            TrecsAssert.IsNotNull(system);
            TrecsAssert.IsNotNull(debugName);
            TrecsAssert.IsNotNull(systemDependencies);

            System = system;
            SystemDependencies = systemDependencies;
            Phase = phase;
            DebugName = debugName;
            Accessor = accessor;
            ExecutionPriority = executionPriority;
        }

        public int? ExecutionPriority { get; }

        /// <summary>
        /// Note that this can be null
        /// </summary>
        public WorldAccessor Accessor { get; }

        public SystemPhase Phase { get; }
        public ISystem System { get; }
        public IReadOnlyCollection<int> SystemDependencies { get; }
        public string DebugName { get; }

        /// <summary>
        /// Position of this system in the original registration order. Stable for the
        /// lifetime of the world; matches the index returned by
        /// <see cref="WorldAccessor.GetSystems"/>. Populated by Trecs during loading,
        /// not by <see cref="ISystemMetadataProvider"/> implementations.
        /// </summary>
        public int DeclarationIndex { get; internal set; }

        public override string ToString()
        {
            return DebugName;
        }
    }
}
