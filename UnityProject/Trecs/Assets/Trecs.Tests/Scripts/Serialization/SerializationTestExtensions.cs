using Trecs.Internal;

namespace Trecs.Tests
{
    /// <summary>
    /// Small test-only conveniences layered on the core serialization primitives
    /// (<see cref="SerializationHelper"/> / <see cref="SerializationData"/> /
    /// <see cref="SerializationReadBuffer"/>) for the byte-level serialization tests — the few
    /// pieces the retired SerializationBuffer used to bundle, expressed directly on the primitives
    /// so the core tests don't depend on that Svkj-only convenience type.
    /// </summary>
    public static class SerializationTestExtensions
    {
        /// <summary>
        /// Copy the contiguous wire form of a just-written payload into a fresh byte array — the
        /// equivalent of the old <c>SerializationBuffer.MemoryStream.ToArray()</c>, for tests that
        /// inspect, corrupt, or truncate the serialized bytes.
        /// </summary>
        public static byte[] ToContiguousBytes(this SerializationData data)
        {
            var bytes = new byte[data.ContiguousSize];
            data.CopyContiguousTo(bytes);
            return bytes;
        }
    }
}
