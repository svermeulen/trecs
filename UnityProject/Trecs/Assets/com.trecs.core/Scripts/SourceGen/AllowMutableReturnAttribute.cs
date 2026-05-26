using System;

namespace Trecs
{
    /// <summary>
    /// Suppression marker for <c>TRECS127</c>. Apply to a method declared on
    /// an <c>[Immutable]</c> interface whose return type is not provably
    /// immutable per the safe-type walker (i.e. would otherwise fire
    /// <c>TRECS127</c>) when the looseness is intentional — the method is a
    /// known escape hatch and the author has thought about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reviewer-facing rationale, when useful, goes in a comment above the
    /// attribute. The analyzer only checks for the attribute's presence.
    /// </para>
    /// <para>
    /// Method-level only; not inherited. Each override / re-declaration must
    /// opt in itself so the escape stays explicit at every declaration site.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [Trecs.Immutable]
    /// public interface IReadOnlyLevelRuntimeInfo
    /// {
    ///     // Safe return — no annotation needed.
    ///     int CellCount { get; }
    ///
    ///     // Escape: returns a mutable concrete; callers can mutate the
    ///     // shared blob through this reference. Shared-mutable by
    ///     // convention; callers must not mutate.
    ///     [Trecs.AllowMutableReturn]
    ///     IterableDictionary&lt;int, List&lt;short&gt;&gt; GetVisualCaveCellIds();
    /// }
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class AllowMutableReturnAttribute : Attribute { }
}
