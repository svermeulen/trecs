using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="UnsafeList{T}"/> of unmanaged elements.
    /// Writes length followed by the underlying memory as a single blit.
    /// A list is allocated on the destination if it was not yet created;
    /// the allocator used in that case is the one passed to the constructor
    /// (default <see cref="Allocator.Persistent"/>).
    /// </summary>
    public sealed class UnsafeListSerializer<T> : ISerializer<UnsafeList<T>>
        where T : unmanaged
    {
        readonly Allocator _allocator;

        public UnsafeListSerializer()
            : this(Allocator.Persistent) { }

        public UnsafeListSerializer(Allocator allocator)
        {
            _allocator = allocator;
        }

        public void Deserialize(ref UnsafeList<T> value, ISerializationReader reader)
        {
            var length = reader.Read<int>("Count");
            TrecsDebugAssert.That(length >= 0);

            if (!value.IsCreated)
            {
                value = new UnsafeList<T>(length, _allocator);
            }

#if DEBUG
            value.Resize(length, NativeArrayOptions.ClearMemory);
#else
            value.Resize(length, NativeArrayOptions.UninitializedMemory);
#endif

            unsafe
            {
                reader.BlitReadArrayPtr("Value", value.Ptr, length);
            }
        }

        public void Serialize(in UnsafeList<T> value, ISerializationWriter writer)
        {
            TrecsDebugAssert.That(value.IsCreated);

            writer.Write("Count", value.Length);

            unsafe
            {
                writer.BlitWriteArrayPtr("Value", value.Ptr, value.Length);
            }
        }

        public void DeserializeObject(ref object value, ISerializationReader reader)
        {
            TrecsDebugAssert.IsNotNull(value, "UnsafeList should always be deserialized in-place");
            var typedValue = (UnsafeList<T>)value;
            Deserialize(ref typedValue, reader);
            value = typedValue;
        }

        public void SerializeObject(object value, ISerializationWriter writer)
        {
            Serialize((UnsafeList<T>)value, writer);
        }
    }
}
