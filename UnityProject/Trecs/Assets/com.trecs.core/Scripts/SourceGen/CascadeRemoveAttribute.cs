using System;

namespace Trecs
{
    /// <summary>
    /// Marks a component field that references other entities so they are
    /// automatically removed when the entity owning the component is removed —
    /// a turnkey cascade delete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported field types:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><see cref="EntityHandle"/> — the single referenced
    ///     entity is removed.</description></item>
    ///   <item><description><c>TrecsList&lt;EntityHandle&gt;</c> — every
    ///     referenced entity in the list is removed.</description></item>
    /// </list>
    /// <para>
    /// Stale handles (already removed) are skipped via
    /// <see cref="EntityHandle.Exists(WorldAccessor)"/>. The cascade completes
    /// within the same <c>Submit()</c> as the owner's removal — queued child
    /// removals drain on subsequent submission iterations — so nested
    /// owner→child→grandchild chains tear down atomically (bounded by
    /// <c>WorldSettings.MaxSubmissionIterations</c>). Cycles are safe because
    /// the underlying remove queue is idempotent.
    /// </para>
    /// <para>
    /// <see cref="CascadeRemoveAttribute"/> only <i>removes the referenced
    /// entities</i>; it never disposes the field itself. Pair it with
    /// <see cref="DisposeOnRemoveAttribute"/> on the same
    /// <c>TrecsList&lt;EntityHandle&gt;</c> field to also free the list's
    /// backing storage. Implies exclusive ownership of the referenced entities.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// partial struct Owner : IEntityComponent
    /// {
    ///     [CascadeRemove, DisposeOnRemove]
    ///     public TrecsList&lt;EntityHandle&gt; Children;
    /// }
    ///
    /// partial struct Child : IEntityComponent
    /// {
    ///     [CascadeRemove]
    ///     public EntityHandle Parent;
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    public sealed class CascadeRemoveAttribute : Attribute { }
}
