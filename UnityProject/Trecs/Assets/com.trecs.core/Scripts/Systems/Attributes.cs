using System;

namespace Trecs
{
    /// <summary>
    /// Constrains a system to execute after the specified systems within the same update phase.
    /// The system scheduler topologically sorts systems using these constraints; cycles cause an assertion failure.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ExecuteAfterAttribute : Attribute
    {
        public ExecuteAfterAttribute(params Type[] systems)
        {
            Systems = systems;
        }

        public Type[] Systems { get; }
    }

    /// <summary>
    /// Constrains a system to execute before the specified systems within the same update phase.
    /// The system scheduler topologically sorts systems using these constraints; cycles cause an assertion failure.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ExecuteBeforeAttribute : Attribute
    {
        public ExecuteBeforeAttribute(params Type[] systems)
        {
            Systems = systems;
        }

        public Type[] Systems { get; }
    }

    /// <summary>
    /// Assigns a system to a specific <see cref="SystemPhase"/>. Without this attribute, systems
    /// default to <see cref="SystemPhase.Fixed"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class PhaseAttribute : Attribute
    {
        public PhaseAttribute(SystemPhase phase)
        {
            Phase = phase;
        }

        public SystemPhase Phase { get; }
    }

    /// <summary>
    /// Sets a numeric priority for tie-breaking when topological ordering is ambiguous.
    /// Higher values execute later within the same update phase. Default priority is 0.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ExecutePriorityAttribute : Attribute
    {
        public ExecutePriorityAttribute(int priority)
        {
            Priority = priority;
        }

        public int Priority { get; }
    }

    /// <summary>
    /// Allows multiple instances of the same system type to be registered in a single world.
    /// Without this attribute, adding a duplicate system type causes an error.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class AllowMultipleAttribute : Attribute { }

    /// <summary>
    /// Controls what happens to an <see cref="InputAttribute"/> component when no input
    /// is provided for a fixed-update frame.
    /// </summary>
    public enum MissingInputFrameBehaviour
    {
        /// <summary>
        /// Resets the component to its <c>default</c> value when no input arrives.
        /// </summary>
        ResetToDefault,

        /// <summary>
        /// Keeps the component's value from the previous frame when no input arrives.
        /// </summary>
        RetainCurrent,
    }

    /// <summary>
    /// Placed on a static interpolation method to source-generate a presentation-phase system
    /// that interpolates a component between fixed-update snapshots each rendered frame.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class GenerateInterpolatorSystemAttribute : Attribute
    {
        public GenerateInterpolatorSystemAttribute(string systemName, string groupName)
        {
            SystemName = systemName;
            GroupName = groupName;
        }

        public string SystemName { get; }
        public string GroupName { get; }
    }
}
