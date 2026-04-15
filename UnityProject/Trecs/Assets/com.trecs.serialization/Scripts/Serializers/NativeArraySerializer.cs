using Trecs.Internal;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Serialization
{
    public class NativeArraySerializer<T> : ISerializer<NativeArray<T>>
        where T : unmanaged
    {
        public NativeArraySerializer() { }

        public void Deserialize(ref NativeArray<T> value, ISerializationReader reader)
        {
            // There are many gotchas with the IsCreated property
            // So for now let's just require that it is always uninitialized
            // Even this check is unreliable since another copy of NativeArray could have
            // disposed it
            Assert.That(!value.IsCreated);

            var length = reader.Read<int>("length");
            Assert.That(length >= 0);

            value = new NativeArray<T>(
                length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory
            );

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
            // There are many gotchas with the IsCreated property
            // So for now let's just require that it is always initialized and never disposed
            // Even this check is unreliable since another copy of NativeArray could have
            // disposed it
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
