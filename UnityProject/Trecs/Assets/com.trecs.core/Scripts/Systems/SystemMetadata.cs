using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs
{
    public enum SystemRunPhase
    {
        Fixed,
        Variable,
        LateVariable,
        Input,
    }

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
