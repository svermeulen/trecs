using System;

namespace Trecs
{
    /// <summary>
    /// Marks a component struct as having a single value member that should be unwrapped
    /// in Aspects. When an Aspect references an unwrapped component, the generated property
    /// exposes the inner field's type directly instead of the component struct.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The component must be a struct, implement <c>IEntityComponent</c>, and have exactly one
    /// instance field. Violating these constraints produces compile-time diagnostics:
    /// </para>
    /// <list type="bullet">
    /// <item><description>TRECS012 — Component must implement IEntityComponent.</description></item>
    /// <item><description>TRECS013 — Component must have exactly one field.</description></item>
    /// </list>
    /// </remarks>
    /// <example>
    /// <code>
    /// [Unwrap]
    /// public partial struct Position : IEntityComponent
    /// {
    ///     public float3 Value;
    /// }
    ///
    /// [Aspect]
    /// partial struct MyView : IWrite&lt;Position&gt; { }
    ///
    /// // With [Unwrap]: myView.Position is ref float3
    /// // Without [Unwrap]: myView.Position would be ref Position
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false)]
    public class UnwrapAttribute : Attribute { }
}
