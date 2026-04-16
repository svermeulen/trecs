using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Tick phase in which a system executes. Determines whether the system runs
    /// on the fixed (deterministic) or variable (render) timeline.
    /// </summary>
    public enum SystemRunPhase
    {
        Fixed,
        Variable,
        LateVariable,
        Input,
    }

    /// <summary>
    /// Runtime descriptor for a registered system, holding its <see cref="ISystem"/> instance,
    /// dependency graph edges, <see cref="SystemRunPhase"/>, and associated <see cref="WorldAccessor"/>.
    /// </summary>
    public class SystemMetadata
    {
        public SystemMetadata(
            ISystem system,
            IReadOnlyCollection<int> systemDependencies,
            SystemRunPhase runPhase,
            WorldAccessor accessor,
            string debugName,
            int? executionPriority
        )
        {
            Assert.IsNotNull(system);
            Assert.IsNotNull(debugName);
            Assert.IsNotNull(systemDependencies);

            System = system;
            SystemDependencies = systemDependencies;
            RunPhase = runPhase;
            DebugName = debugName;
            Accessor = accessor;
            ExecutionPriority = executionPriority;
        }

        public int? ExecutionPriority { get; }

        /// <summary>
        /// Note that this can be null
        /// </summary>
        public WorldAccessor Accessor { get; }

        public SystemRunPhase RunPhase { get; }
        public ISystem System { get; }
        public IReadOnlyCollection<int> SystemDependencies { get; }
        public string DebugName { get; }

        public override string ToString()
        {
            // Useful when getting errors during topological sorting
            return System.GetType().ToString();
        }
    }
}
