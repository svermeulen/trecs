using System;

namespace Trecs
{
    /// <summary>
    /// Marks a parameter on a system <c>[ForEachEntity]</c> method (or a method with
    /// per-parameter <c>[SingleEntity]</c>) as a user-supplied pass-through argument
    /// that should be forwarded by the generated overloads, rather than auto-detected
    /// as an iteration parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Most pass-through arguments do not need this attribute — by default, any parameter
    /// whose type is not an <c>IEntityComponent</c>, an <c>IAspect</c>, an
    /// <c>EntityIndex</c>, or a <c>WorldAccessor</c> is treated as a pass-through arg.
    /// You only need <c>[PassThroughArgument]</c> when the parameter's type would
    /// otherwise be auto-detected as one of those special types — i.e. it implements
    /// <c>IEntityComponent</c>, or it is exactly <c>Trecs.EntityIndex</c>, or it is
    /// exactly <c>Trecs.WorldAccessor</c>. The attribute disambiguates "user-supplied
    /// value" from "loop-supplied value".
    /// </para>
    /// <para>
    /// Parameters in an iteration method may appear in any order — the generator
    /// emits the call to the user method preserving the original declaration order.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Most custom args need no attribute (primitive type, not auto-detected):
    /// [ForEachEntity(typeof(MyTag))]
    /// void Step(ref CPosition pos, float dt) { pos.Value += dt; }
    ///
    /// // IEntityComponent passed by value as a pass-through arg requires the attribute:
    /// [ForEachEntity(typeof(MyTag))]
    /// void Step(ref CPosition pos, [PassThroughArgument] CDefaults defaults) { /* ... */ }
    ///
    /// // User-supplied EntityIndex (overrides loop's iteration index, or supplements it):
    /// [ForEachEntity(typeof(MyTag))]
    /// void Step(ref CPosition pos, [PassThroughArgument] EntityIndex target) { /* ... */ }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class PassThroughArgumentAttribute : Attribute { }
}
