using System;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Marks a <c>static</c> <c>[ForEachEntity]</c> method on a system class for automatic
    /// Burst-compiled parallel job generation. The source generator wraps the method as a
    /// job: it emits a nested <c>[BurstCompile] partial struct</c> whose <c>Execute</c>
    /// calls the user's static method, plus a scheduling wrapper on the system class.
    /// <para>
    /// The method must be <c>static</c> so the C# compiler enforces that no instance state
    /// (like <c>World</c>) is accessed — errors appear in user code, not generated code.
    /// </para>
    /// <para>
    /// <b>Supported parameter types:</b>
    /// <list type="bullet">
    /// <item>Aspect (<c>in MyAspect</c>) or component refs (<c>in CPos</c>, <c>ref CVel</c>)</item>
    /// <item><c>EntityIndex</c> — auto-injected from the iteration loop</item>
    /// <item><c>in NativeWorldAccessor</c> — auto-injected via <c>World.ToNative()</c></item>
    /// <item><c>in NativeSetRead&lt;TSet&gt;</c> / <c>in NativeSetCommandBuffer&lt;TSet&gt;</c> — auto-injected set accessors for deferred set operations</item>
    /// <item><c>[PassThroughArgument]</c> parameters — become job fields, supplied at call site</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <example>
    /// <code>
    /// [ForEachEntity(typeof(FrenzyTags.Fish))]
    /// [WrapAsJob]
    /// static void Move(in Fish fish, in NativeWorldAccessor world)
    /// {
    ///     fish.Position += world.DeltaTime * fish.Velocity;
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class WrapAsJobAttribute : Attribute { }
}
