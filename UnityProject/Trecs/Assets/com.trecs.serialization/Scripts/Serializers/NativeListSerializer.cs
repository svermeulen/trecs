using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Serializer for <see cref="NativeList{T}"/> of unmanaged elements.
    /// Writes length followed by the underlying memory as a single blit.
    /// A list is allocated on the destination if it was not yet created;
    /// the allocator used in that case is the one passed to the constructor
    /// (default <see cref="Allocator.Persistent"/>).
    /// </summary>
    public sealed class NativeListSerializer<T> : ISerializer<NativeList<T>>
        where T : unmanaged
    {
        readonly Allocator _allocator;

        public NativeListSerializer()
            : this(Allocator.Persistent) { }

        public NativeListSerializer(Allocator allocator)
        {
            _allocator = allocator;
        }

        public void Deserialize(ref NativeList<T> value, ISerializationReader reader)
        {
            var length = reader.Read<int>("length");
            Assert.That(length >= 0);

            // IsCreated is best-effort but sufficient to drive the alloc-or-reuse
            // branch — `new NativeList(length, ...)` only reserves capacity, so
            // we still need the explicit Resize below in either branch.
            if (!value.IsCreated)
            {
                value = new NativeList<T>(length, _allocator);
            }

#if DEBUG
            value.Resize(length, NativeArrayOptions.ClearMemory);
#else
            value.Resize(length, NativeArrayOptions.UninitializedMemory);
#endif

            unsafe
            {
                reader.BlitReadArrayPtr(
                    "value",
                    NativeListUnsafeUtility.GetUnsafePtr(value),
                    length
                );
            }
        }

        public void Serialize(in NativeList<T> value, ISerializationWriter writer)
        {
            Assert.That(value.IsCreated);

            writer.Write("length", value.Length);

            unsafe
            {
                writer.BlitWriteArrayPtr(
                    "value",
                    NativeListUnsafeUtility.GetUnsafeReadOnlyPtr(value),
                    value.Length
                );
            }
        }

        public void DeserializeObject(ref object value, ISerializationReader reader)
        {
            Assert.IsNotNull(value, "NativeList should always be deserialized in-place");
            var typedValue = (NativeList<T>)value;
            Deserialize(ref typedValue, reader);
            value = typedValue;
        }

        public void SerializeObject(object value, ISerializationWriter writer)
        {
            Serialize((NativeList<T>)value, writer);
        }
    }
}
