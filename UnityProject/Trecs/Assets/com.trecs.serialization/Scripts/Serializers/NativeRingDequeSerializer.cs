using Trecs.Internal;
using Unity.Collections;

namespace Trecs.Serialization
{
    public class NativeRingDequeSerializer<T> : ISerializer<NativeRingDeque<T>>
        where T : unmanaged
    {
        public NativeRingDequeSerializer() { }

        public void Deserialize(ref NativeRingDeque<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("numItems");
            Assert.That(numItems >= 0);

            if (!value.IsCreated)
            {
                var initialCapacity = numItems > 0 ? numItems : NativeRingDeque<T>.DefaultCapacity;
                value = new NativeRingDeque<T>(initialCapacity, Allocator.Persistent);
            }
            else
            {
                Assert.That(value.IsEmpty);
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

        public void Serialize(in NativeRingDeque<T> value, ISerializationWriter writer)
        {
            writer.Write("numItems", value.Length);

            foreach (var item in value)
            {
                writer.Write("item", item);
            }
        }
    }
}
