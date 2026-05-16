using Unity.Collections;

namespace Trecs
{
    /// <summary>
    /// Override hook for serializing the component array of a single component
    /// type. Register an implementation via
    /// <see cref="ComponentArraySerializerRegistry.Register{T}"/>.
    ///
    /// Use cases:
    /// <list type="bullet">
    ///   <item>A component holds a native container (e.g. <c>UnsafeHashMap</c>,
    ///         <c>UnsafeList</c>) that can't be byte-blitted.</item>
    ///   <item>A component holds transient single-frame state that should be
    ///         excluded from snapshots / checksums entirely (see
    ///         <see cref="SkipComponentSerializer{T}"/>).</item>
    ///   <item>A component wraps a runtime simulation handle that should be
    ///         reset on load rather than restored.</item>
    /// </list>
    ///
    /// The framework owns the entry count for every component array in a
    /// group — all arrays must have the same length (one entry per entity).
    /// The framework writes/reads the count itself, so implementations only
    /// need to serialize per-element data.
    ///
    /// On <see cref="Serialize"/> the implementation receives a
    /// <see cref="NativeList{T}"/> whose <c>Length</c> equals the current
    /// entity count. Skipping a value (e.g. for checksum streams) is done by
    /// inspecting <see cref="ISerializationWriter.Flags"/> and writing
    /// nothing — the framework never deserializes checksum streams, so the
    /// read side is safe.
    ///
    /// On <see cref="Deserialize"/> the list arrives with the **live world's**
    /// current count as <c>Length</c>, and <paramref name="requiredCount"/>
    /// gives the count the array must have on return. The implementation
    /// decides how to reconcile any mismatch (preserve in place when counts
    /// match, resize-and-default-init, dispose-and-rebuild, etc.). The
    /// dispatcher asserts that <c>values.Length == requiredCount</c> on
    /// return and adopts that as the new component-array count.
    /// </summary>
    public interface IComponentArraySerializer<T>
        where T : unmanaged, IEntityComponent
    {
        void Serialize(NativeList<T> values, ISerializationWriter writer);
        void Deserialize(NativeList<T> values, int requiredCount, ISerializationReader reader);
    }
}
