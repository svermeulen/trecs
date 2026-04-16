using System;

namespace Trecs
{
    /// <summary>
    /// Marks a field on a Trecs job struct as source-generator-populated. The
    /// generator wires the field's value before scheduling and tracks the
    /// corresponding read/write dependency on the runtime job scheduler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Inline tags</b> — When <see cref="Tag"/> or <see cref="Tags"/> is
    /// specified, the schedule method still has a parameter for the field, but
    /// it is <b>optional</b> (<c>TagSet? = null</c>). When null, only the
    /// inline tags are used. When provided, the runtime tags are combined with
    /// the inline tags via <c>TagSet.CombineWith</c>. This allows callers to
    /// refine the query at schedule time (e.g. adding a partition tag).
    /// </para>
    /// <para>
    /// <b>No inline tags</b> — When neither <see cref="Tag"/> nor
    /// <see cref="Tags"/> is specified, the field becomes a <b>mandatory</b>
    /// parameter on the generated method. The parameter type depends on the
    /// field type:
    /// </para>
    /// <list type="table">
    /// <listheader>
    /// <term>Field type</term>
    /// <description>Schedule parameter</description>
    /// </listheader>
    /// <item><term><see cref="NativeComponentBufferRead{T}"/> /
    /// <see cref="NativeComponentBufferWrite{T}"/></term>
    /// <description><see cref="TagSet"/> — must resolve to exactly one
    /// group; loud assertion at runtime if not.</description></item>
    /// <item><term><see cref="NativeComponentRead{T}"/> /
    /// <see cref="NativeComponentWrite{T}"/></term>
    /// <description><see cref="EntityIndex"/>. Inline tags are not
    /// supported for these types.</description></item>
    /// <item><term><see cref="NativeComponentLookupRead{T}"/> /
    /// <see cref="NativeComponentLookupWrite{T}"/></term>
    /// <description><see cref="TagSet"/> — may resolve to multiple groups;
    /// each contributes a per-group dependency.</description></item>
    /// <item><term><c>NativeSetWrite&lt;TSet&gt;</c></term>
    /// <description>(no extra parameter — the set type is on the field's
    /// generic argument).</description></item>
    /// <item><term><c>NativeSetRead&lt;TSet&gt;</c></term>
    /// <description>(no extra parameter — read + deferred set ops).</description></item>
    /// <item><term><c>NativeEntitySetIndices&lt;TSet&gt;</c></term>
    /// <description><see cref="TagSet"/> — must resolve to exactly one
    /// group.</description></item>
    /// <item><term><see cref="NativeWorldAccessor"/></term>
    /// <description>(no extra parameter — wired via
    /// <c>world.ToNative()</c>). Tags are not supported.</description></item>
    /// <item><term><see cref="Group"/></term>
    /// <description><see cref="TagSet"/> — must resolve to exactly one
    /// group via <c>GetSingleGroupWithTags</c>.</description></item>
    /// </list>
    /// <para>
    /// For runtime-tag fields, the schedule-method parameter name is derived
    /// from the field name. For
    /// <c>[FromWorld] NativeComponentLookupRead&lt;CPosition&gt; AllPositions;</c>
    /// the parameter becomes <c>TagSet allPositionsTags</c>, so call sites
    /// pair cleanly with named arguments.
    /// </para>
    /// </remarks>
    /// <example>
    /// Inline tags (optional schedule parameter):
    /// <code>
    /// [BurstCompile]
    /// partial struct ScatterJob
    /// {
    ///     [ReadOnly]
    ///     [FromWorld(Tag = typeof(FrenzyTags.Fish))]
    ///     public NativeComponentBufferRead&lt;CPosition&gt; FishPositions;
    ///
    ///     public void Execute(int i) { /* ... */ }
    /// }
    ///
    /// // Generated: ScheduleParallel(WorldAccessor world, int count, TagSet? fishPositionsTags = null, ...)
    /// // Use inline tags only:
    /// new ScatterJob().ScheduleParallel(World, count: n);
    /// // Combine with extra tags at schedule time:
    /// new ScatterJob().ScheduleParallel(World, count: n, fishPositionsTags: TagSet&lt;FrenzyTags.Eating&gt;.Value);
    /// </code>
    ///
    /// No inline tags (mandatory schedule parameter):
    /// <code>
    /// [BurstCompile]
    /// partial struct ChaseJob
    /// {
    ///     [FromWorld]
    ///     public NativeComponentLookupRead&lt;CPosition&gt; AllPositions;
    ///
    ///     [ForEachEntity(Tag = typeof(FrenzyTags.Fish))]
    ///     void Execute(in Fish fish) { /* ... */ }
    /// }
    ///
    /// // Generated: ScheduleParallel(WorldAccessor world, TagSet allPositionsTags, ...)
    /// </code>
    /// </example>
    [AttributeUsage(
        AttributeTargets.Field | AttributeTargets.Parameter,
        AllowMultiple = false,
        Inherited = false
    )]
    public sealed class FromWorldAttribute : Attribute
    {
        /// <summary>
        /// Tag types to scope group resolution. Use for multiple tags; use
        /// <see cref="Tag"/> for a single tag.
        /// </summary>
        public Type[] Tags { get; set; }

        /// <summary>
        /// Single tag type to scope group resolution. Shorthand for
        /// <c>Tags = new[] { typeof(T) }</c>. Cannot be combined with
        /// <see cref="Tags"/>.
        /// </summary>
        public Type Tag { get; set; }

        public FromWorldAttribute()
        {
            Tags = Array.Empty<Type>();
        }
    }
}
