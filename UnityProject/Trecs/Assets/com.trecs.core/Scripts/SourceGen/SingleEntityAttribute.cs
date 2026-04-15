using System;

namespace Trecs
{
    /// <summary>
    /// Like <see cref="ForEachEntityAttribute"/>, but additionally asserts that exactly one
    /// entity matches the query at runtime. The source generator emits the same iteration
    /// loop as <c>[ForEachEntity]</c>, then a <c>Assert.That(count == 1, …)</c> after the
    /// loop completes.
    /// <para>
    /// Use case: methods that operate on a global / singleton entity. Replaces the
    /// pre-Phase-5 <c>[ForSingleAspect]</c> / <c>[ForSingleComponents]</c> attributes.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class SingleEntityAttribute : Attribute
    {
        /// <summary>
        /// Tag types to scope the query. Use for multiple tags; use <see cref="Tag"/>
        /// for a single tag.
        /// </summary>
        public Type[] Tags { get; set; }

        /// <summary>
        /// Single tag type to scope the query. Shorthand for
        /// <c>Tags = new[] { typeof(T) }</c>. Cannot be combined with <see cref="Tags"/>.
        /// </summary>
        public Type Tag { get; set; }

        /// <summary>
        /// Optional set type to restrict iteration to entities in the specified set.
        /// </summary>
        public Type Set { get; set; }

        /// <summary>
        /// When true, matches groups by which components they declare rather than by
        /// requiring tag membership.
        /// </summary>
        public bool MatchByComponents { get; set; }

        public SingleEntityAttribute()
        {
            Tags = Array.Empty<Type>();
        }
    }
}
