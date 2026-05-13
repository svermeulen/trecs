using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Serializer for <see cref="NativeArray{T}"/> of unmanaged elements.
    /// Writes length followed by the underlying memory as a single blit.
    /// The deserialized array is allocated with the allocator passed to the
    /// constructor (default <see cref="Allocator.Persistent"/>).
    /// </summary>
    public sealed class NativeArraySerializer<T> : ISerializer<NativeArray<T>>
        where T : unmanaged
    {
        readonly Allocator _allocator;

        public NativeArraySerializer()
            : this(Allocator.Persistent) { }

        public NativeArraySerializer(Allocator allocator)
        {
            _allocator = allocator;
        }

        public void Deserialize(ref NativeArray<T> value, ISerializationReader reader)
        {
            // IsCreated is best-effort (a separate alias may have disposed it),
            // but the assert at least catches the "I forgot to clear before
            // deserializing" mistake which would otherwise leak the prior allocation.
            Assert.That(!value.IsCreated);

            var length = reader.Read<int>("length");
            Assert.That(length >= 0);

            value = new NativeArray<T>(length, _allocator, NativeArrayOptions.UninitializedMemory);

            unsafe
            {
                reader.BlitReadArrayPtr(
                    "value",
                    (T*)NativeArrayUnsafeUtility.GetUnsafePtr(value),
                    length
                );
            }
        }

        public void Serialize(in NativeArray<T> value, ISerializationWriter writer)
        {
            Assert.That(value.IsCreated);

            writer.Write("length", value.Length);

            unsafe
            {
                writer.BlitWriteArrayPtr(
                    "value",
                    (T*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(value),
                    value.Length
                );
            }
        }

        public void DeserializeObject(ref object value, ISerializationReader reader)
        {
            Assert.IsNotNull(value, "NativeArray should always be deserialized in-place");
            var typedValue = (NativeArray<T>)value;
            Deserialize(ref typedValue, reader);
            value = typedValue;
        }

        public void SerializeObject(object value, ISerializationWriter writer)
        {
            Serialize((NativeArray<T>)value, writer);
        }
    }
}
