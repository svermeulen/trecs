using System;

namespace Trecs
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ExecutesAfterAttribute : Attribute
    {
        public ExecutesAfterAttribute(params Type[] systems)
        {
            Systems = systems;
        }

        public Type[] Systems { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class ExecutesBeforeAttribute : Attribute
    {
        public ExecutesBeforeAttribute(params Type[] systems)
        {
            Systems = systems;
        }

        public Type[] Systems { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class VariableUpdateAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class LateVariableUpdateAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class InputSystemAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false)]
    public class SuppressNoReferenceWarningAttribute : Attribute { }

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
    /// Add to systems that you want multiple instances of
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class AllowMultipleAttribute : Attribute { }

    public enum MissingInputFrameBehaviour
    {
        ResetToDefault,
        RetainCurrent,
    }

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
