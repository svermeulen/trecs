using System;

namespace Trecs
{
    /// <summary>
    /// Marks an interface as an Aspect interface that can provide shared component access.
    /// Aspect interfaces define common sets of components that can be implemented by multiple Aspects.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface, AllowMultiple = false)]
    public class AspectInterfaceAttribute : Attribute { }
}
