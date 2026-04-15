#nullable enable

using Microsoft.CodeAnalysis;
using Trecs.SourceGen.Performance;

namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Phase 5 routing helper for the unified <c>[ForEachEntity]</c> /
    /// <c>[SingleEntity]</c> iteration markers.
    /// <para>
    /// In Phase 5, <c>[ForEachEntity]</c> replaces <c>[ForEachAspect]</c> /
    /// <c>[ForEachComponents]</c>, and <c>[SingleEntity]</c> replaces
    /// <c>[ForSingleAspect]</c> / <c>[ForSingleComponents]</c>. The aspect-vs-components
    /// kind is no longer carried by the attribute name; it's determined by inspecting
    /// the method's parameter types.
    /// </para>
    /// <para>
    /// During the migration period the old attributes still work. Each existing
    /// generator extends its predicate to also accept the new attribute name(s) and
    /// uses these helpers to decide whether to claim a method based on parameter shape.
    /// </para>
    /// </summary>
    internal static class IterationAttributeRouting
    {
        /// <summary>
        /// Returns true if the method has at least one iteration-target parameter whose
        /// type implements <c>Trecs.IAspect</c>. Used by aspect-iteration generators to
        /// claim <c>[ForEachEntity]</c> / <c>[SingleEntity]</c> methods that have an
        /// aspect parameter, and by component-iteration generators to skip them.
        /// <para>
        /// Parameters marked <c>[PassThroughArgument]</c> are skipped — they're forwarded
        /// from the call site verbatim and aren't iteration targets, even if their type
        /// happens to implement <c>IAspect</c>. Without this skip, a user-supplied
        /// pass-through aspect param would mis-route the method into the aspect generator
        /// and break code-gen.
        /// </para>
        /// </summary>
        public static bool HasAspectParameter(IMethodSymbol method)
        {
            foreach (var p in method.Parameters)
            {
                if (
                    PerformanceCache.HasAttributeByName(
                        p,
                        TrecsAttributeNames.PassThroughArgument,
                        TrecsNamespaces.Trecs
                    )
                )
                    continue;
                if (SymbolAnalyzer.ImplementsInterface(p.Type, "IAspect", TrecsNamespaces.Trecs))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// True if the method carries the modern <c>[ForEachEntity]</c> attribute.
        /// </summary>
        public static bool HasEntityFilter(IMethodSymbol method) =>
            PerformanceCache.HasAttributeByName(
                method,
                TrecsAttributeNames.EntityFilter,
                TrecsNamespaces.Trecs
            );

        /// <summary>
        /// True if the method carries the modern <c>[SingleEntity]</c> attribute.
        /// </summary>
        public static bool HasSingleEntity(IMethodSymbol method) =>
            PerformanceCache.HasAttributeByName(
                method,
                TrecsAttributeNames.SingleEntity,
                TrecsNamespaces.Trecs
            );

        /// <summary>
        /// True if a method that wears <c>[ForEachEntity]</c> should be claimed by an
        /// aspect-iteration generator. Returns true iff at least one parameter
        /// implements <c>IAspect</c>.
        /// </summary>
        public static bool RoutesToAspectGenerator(IMethodSymbol method) =>
            HasAspectParameter(method);

        /// <summary>
        /// True if a method that wears <c>[ForEachEntity]</c> should be claimed by a
        /// component-iteration generator. Returns true iff no parameter implements
        /// <c>IAspect</c>.
        /// </summary>
        public static bool RoutesToComponentsGenerator(IMethodSymbol method) =>
            !HasAspectParameter(method);

        /// <summary>
        /// True if the method carries the <c>[WrapAsJob]</c> attribute. Methods with this
        /// attribute are claimed by the AutoJobGenerator and should be skipped by
        /// ForEachAspect/IncrementalForEach generators.
        /// </summary>
        public static bool HasWrapAsJobAttribute(IMethodSymbol method) =>
            PerformanceCache.HasAttributeByName(
                method,
                TrecsAttributeNames.WrapAsJob,
                TrecsNamespaces.Trecs
            );
    }
}
