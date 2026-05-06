namespace Trecs.SourceGen.Shared
{
    /// <summary>
    /// Captures the four pieces of an outer-type partial declaration the
    /// generators need to emit a wrapper that merges with the user's
    /// original: the bare name, kind (class/struct), accessibility, and
    /// type-parameter list (e.g. <c>"&lt;T&gt;"</c> or empty). Carried as
    /// a value-equality record so it round-trips through Roslyn's
    /// incremental-pipeline cache.
    /// </summary>
    internal readonly record struct ContainingTypeInfo(
        string Name,
        string Kind,
        string Accessibility,
        string TypeParameterList
    );
}
