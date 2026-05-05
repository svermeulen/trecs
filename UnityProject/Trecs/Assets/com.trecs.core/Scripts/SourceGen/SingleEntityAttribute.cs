using System;

namespace Trecs
{
    /// <summary>
    /// Marks a parameter (or job-struct field) that should be resolved to the unique
    /// entity matching the given tag(s). The framework runs the equivalent of
    /// <c>World.Query().WithTags&lt;...&gt;().Single()</c> before the user method body,
    /// asserts exactly one match, and binds the result to the parameter / field.
    /// <para>
    /// Aspect-typed parameters get a materialized aspect view; component-typed
    /// parameters (<c>in</c> / <c>ref</c>) get the matching component value.
    /// </para>
    /// <para>
    /// Inline tags are mandatory — there is no runtime override and no
    /// <c>Optional</c> mode (an aspect can't be null). Use plain
    /// <c>World.Query()...Single()</c> in code if you need a runtime-supplied query.
    /// </para>
    /// <para>
    /// Works in four contexts: plain Execute methods (singleton parameters trigger
    /// run-once codegen), <c>[ForEachEntity]</c> methods (singletons hoist out of
    /// the loop), <c>[WrapAsJob]</c> static methods (singletons become job fields),
    /// and hand-written job-struct fields.
    /// </para>
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Parameter | AttributeTargets.Field,
        AllowMultiple = false,
        Inherited = false
    )]
    public sealed class SingleEntityAttribute : Attribute
    {
        /// <summary>
        /// Tag types to scope the singleton query. Use for multiple tags; use
        /// <see cref="Tag"/> for a single tag.
        /// </summary>
        public Type[] Tags { get; set; }

        /// <summary>
        /// Single tag type to scope the singleton query. Shorthand for
        /// <c>Tags = new[] { typeof(T) }</c>. Cannot be combined with <see cref="Tags"/>.
        /// </summary>
        public Type Tag { get; set; }

        public SingleEntityAttribute()
        {
            Tags = Array.Empty<Type>();
        }
    }
}
