using System;

namespace Trecs
{
    /// <summary>
    /// Marks a method as an entity-iteration target.
    /// <para>
    /// <c>[ForEachEntity]</c> is the explicit, unambiguous marker that the method should
    /// be source-generated as an iteration loop. The aspect parameter (<c>in MyAspect</c>)
    /// or component ref/in parameters become per-entity iteration targets; every other
    /// parameter (<c>EntityIndex</c>, <c>WorldAccessor</c>, <c>[GlobalIndex] int</c>,
    /// <c>[PassThroughArgument]</c> values, …) is classified by the existing source-gen
    /// rules.
    /// </para>
    /// <para>
    /// When the attribute is empty, the source-gen emits a <c>QueryBuilder</c> overload
    /// that the caller supplies at the call site.
    /// </para>
    /// <para>
    /// For declaring which groups a <c>[FromWorld]</c> field should access, use the
    /// <c>Tag</c>/<c>Tags</c> properties on <see cref="FromWorldAttribute"/> instead.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class ForEachEntityAttribute : Attribute
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
        /// Tag type to exclude from the query — entities tagged with this will not
        /// be iterated. Use to query the "absent" partition of a presence/absence
        /// dimension declared via <c>IPartitionedBy&lt;T&gt;</c>. For multiple
        /// excluded tags, use <see cref="Withouts"/>.
        /// </summary>
        public Type Without { get; set; }

        /// <summary>
        /// Tag types to exclude from the query — entities tagged with any of these
        /// will not be iterated. Use for multiple exclusions; use
        /// <see cref="Without"/> for a single exclusion.
        /// </summary>
        public Type[] Withouts { get; set; }

        /// <summary>
        /// When true, matches groups by which components they declare rather than by
        /// requiring tag membership.
        /// </summary>
        public bool MatchByComponents { get; set; }

        public ForEachEntityAttribute()
        {
            Tags = Array.Empty<Type>();
        }

        /// <summary>
        /// Shorthand: <c>[ForEachEntity(typeof(MyTag))]</c> /
        /// <c>[ForEachEntity(typeof(A), typeof(B))]</c>. Equivalent to setting
        /// <see cref="Tags"/>. Cannot be combined with the named <see cref="Tag"/>
        /// / <see cref="Tags"/> properties.
        /// </summary>
        public ForEachEntityAttribute(params Type[] tags)
        {
            Tags = tags ?? Array.Empty<Type>();
        }
    }
}
