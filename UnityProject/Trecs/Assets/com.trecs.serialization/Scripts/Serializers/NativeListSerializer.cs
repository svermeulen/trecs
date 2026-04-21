using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="NativeList{T}"/> of unmanaged elements.
    /// Writes length followed by the underlying memory as a single blit.
    /// </summary>
    public class NativeListSerializer<T> : ISerializer<NativeList<T>>
        where T : unmanaged
    {
        public NativeListSerializer() { }

        public void Deserialize(ref NativeList<T> value, ISerializationReader reader)
        {
            var length = reader.Read<int>("length");
            Assert.That(length >= 0);

            // Note that there are many gotchas with the IsCreated property
            // It is unreliable since another copy of NativeList could have
            // disposed it
            // Here we assume it is accurate though
            // Note also that it isn't sufficient to just 'new NativeList'
            // here since the capacity will be correct but length will
            // be zero
            if (!value.IsCreated)
            {
                value = new NativeList<T>(length, Allocator.Persistent);
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
            // There are many gotchas with the IsCreated property
            // So for now let's just require that it is always initialized and never disposed
            // Even this check is unreliable since another copy of NativeList could have
            // disposed it
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
