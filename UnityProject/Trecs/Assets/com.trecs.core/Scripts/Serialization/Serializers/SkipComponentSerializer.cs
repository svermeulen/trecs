using Trecs.Internal;
using Unity.Collections;

namespace Trecs.Serialization
{
    /// <summary>
    /// Drop-in <see cref="IComponentArraySerializer{T}"/> that skips writing the
    /// component's values to the stream and leaves the existing array contents
    /// untouched on load. Register it for component types whose data should
    /// be excluded from snapshots, recordings, and checksums — transient
    /// single-frame buffers, broadphase scratchpads, runtime handles whose
    /// state isn't meaningful to persist:
    /// <code>
    /// world.ComponentArraySerializerRegistry.Register(new SkipComponentSerializer&lt;CMyTransient&gt;());
    /// </code>
    /// The values themselves contribute nothing to the stream's bytes — and
    /// therefore nothing to the checksum hash either — so checksums won't
    /// desync on values that vary between runs. On load the current runtime
    /// values pass through unchanged.
    ///
    /// The framework writes the entry count (a single <c>int</c>) for every
    /// component array and asserts a count match on load: a snapshot taken
    /// when the group had <i>N</i> entities must be restored into a live
    /// world where the group also has <i>N</i> entities. Group component
    /// arrays are expected to stay in lockstep, so a count mismatch
    /// indicates a buggy snapshot — failing loudly here is much kinder than
    /// silently producing entities whose components are partially live and
    /// partially garbage.
    ///
    /// If you need a serializer that tolerates count mismatches — typical
    /// for fresh-load-from-disk scenarios where the live world starts empty —
    /// use <see cref="DefaultValueComponentSerializer{T}"/> instead, which
    /// resizes to match the snapshot and zero-inits new entries.
    /// </summary>
    public sealed class SkipComponentSerializer<T> : IComponentArraySerializer<T>
        where T : unmanaged, IEntityComponent
    {
        public void Serialize(NativeList<T> values, ISerializationWriter writer) { }

        public void Deserialize(
            NativeList<T> values,
            int requiredCount,
            ISerializationReader reader
        )
        {
            TrecsAssert.That(
                requiredCount == values.Length,
                "SkipComponentSerializer<{0}>: snapshot has {1} entries for this group but the live world has {2}. The component is excluded from the stream, so its entries must already line up with the rest of the group when the snapshot is restored.",
                typeof(T).Name,
                requiredCount,
                values.Length
            );
        }
    }
}
