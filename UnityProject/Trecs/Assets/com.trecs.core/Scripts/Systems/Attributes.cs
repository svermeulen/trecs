using System;

namespace Trecs
{
    /// <summary>
    /// Constrains a system to execute after the specified systems within the same update phase.
    /// The system scheduler topologically sorts systems using these constraints; cycles cause an assertion failure.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ExecutesAfterAttribute : Attribute
    {
        public ExecutesAfterAttribute(params Type[] systems)
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
    public class ExecutesBeforeAttribute : Attribute
    {
        public ExecutesBeforeAttribute(params Type[] systems)
        {
            Systems = systems;
        }

        public Type[] Systems { get; }
    }

    /// <summary>
    /// Assigns a system to the variable-update phase, which runs once per rendered frame
    /// with a variable time step. Without this attribute, systems default to fixed-update.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class VariableUpdateAttribute : Attribute { }

    /// <summary>
    /// Assigns a system to the late-variable-update phase, which runs after all
    /// variable-update systems each rendered frame. Useful for post-render corrections.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class LateVariableUpdateAttribute : Attribute { }

    /// <summary>
    /// Marks a system as an input system, which runs at the start of every
    /// fixed-update tick (so zero-to-many times per rendered frame, matching
    /// the fixed-tick cadence). Input systems can call <see cref="WorldAccessor.AddInput{T}"/>
    /// to enqueue input that the fixed-update simulation will consume deterministically.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class InputSystemAttribute : Attribute { }

    /// <summary>
    /// Suppresses the "no reference found" build warning for a component struct that is
    /// only referenced indirectly (e.g. via source generation or generic template definitions).
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false)]
    public class SuppressNoReferenceWarningAttribute : Attribute { }

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
    /// Placed on a static interpolation method to source-generate a variable-update system
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
