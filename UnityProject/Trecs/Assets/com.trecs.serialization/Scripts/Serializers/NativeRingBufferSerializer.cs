using Trecs.Internal;

namespace Trecs.Serialization
{
    public class NativeRingBufferSerializer<T> : ISerializer<NativeRingBuffer<T>>
        where T : unmanaged
    {
        public NativeRingBufferSerializer() { }

        public void Deserialize(ref NativeRingBuffer<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("numItems");
            Assert.That(numItems >= 0);

            if (value == null)
            {
                value = new NativeRingBuffer<T>(
                    numItems > 0 ? numItems : NativeRingBuffer<T>.DefaultCapacity
                );
            }
            else
            {
                Assert.That(value.IsEmpty());
                value.Clear();
                value.EnsureCapacity(numItems);
            }

            for (int i = 0; i < numItems; i++)
            {
                T item = default;
                reader.Read("item", ref item);
                value.PushBack(item);
            }
        }

        public void Serialize(in NativeRingBuffer<T> value, ISerializationWriter writer)
        {
            writer.Write("numItems", value.Count);

            foreach (var item in value)
            {
                writer.Write("item", item);
            }
        }
    }
}
