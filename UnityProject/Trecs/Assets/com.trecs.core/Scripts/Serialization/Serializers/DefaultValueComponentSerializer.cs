using Unity.Collections;

namespace Trecs.Serialization
{
    /// <summary>
    /// Drop-in <see cref="IComponentArraySerializer{T}"/> that skips writing the
    /// component's values to the stream and resets every entry to
    /// <c>default(T)</c> on load. Use it for components whose values don't
    /// need to round-trip through snapshots and are safe (or expected) to
    /// be re-initialized at runtime — e.g. transient single-frame buffers
    /// whose entries get re-populated each tick anyway, or runtime handles
    /// that the user's systems will set up via OnAdded handlers.
    ///
    /// Compared to <see cref="SkipComponentSerializer{T}"/>:
    /// <list type="bullet">
    ///   <item><c>SkipComponentSerializer</c> preserves the live runtime values
    ///         across load and <b>asserts</b> the entry count matches the
    ///         snapshot's. Right when both sides have the same entities and
    ///         you want to keep their runtime state intact (the typical
    ///         in-session save/restore case).</item>
    ///   <item><c>DefaultValueComponentSerializer</c> resizes the live array
    ///         to the snapshot's count and zero-inits every entry. Right
    ///         when the live world's entity set differs from the snapshot's
    ///         (fresh-load from disk, dynamically respawned entities, etc.)
    ///         and the values don't need to survive — they'll be initialized
    ///         from elsewhere.</item>
    /// </list>
    ///
    /// The framework writes the entry count (a single <c>int</c>) — that's
    /// how the component array gets sized to match the rest of the group on
    /// load. The element values themselves contribute nothing to the stream
    /// and therefore nothing to the checksum hash.
    /// </summary>
    public sealed class DefaultValueComponentSerializer<T> : IComponentArraySerializer<T>
        where T : unmanaged, IEntityComponent
    {
        public void Serialize(NativeList<T> values, ISerializationWriter writer) { }

        public void Deserialize(
            NativeList<T> values,
            int requiredCount,
            ISerializationReader reader
        )
        {
            // Clear first so the Resize-with-ClearMemory zero-inits every
            // position (the Length setter only zeros positions past oldLength).
            values.Clear();
            values.Resize(requiredCount, NativeArrayOptions.ClearMemory);
        }
    }
}
