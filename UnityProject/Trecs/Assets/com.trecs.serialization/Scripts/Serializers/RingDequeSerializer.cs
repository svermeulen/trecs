using Trecs.Internal;

namespace Trecs.Serialization
{
    public sealed class RingDequeSerializer<T> : ISerializer<RingDeque<T>>
    {
        public RingDequeSerializer() { }

        public void Deserialize(ref RingDeque<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("count");
            Assert.That(numItems >= 0);
            Assert.That(
                numItems <= 1000000,
                "Unexpectedly large number of items in ring deque ({}).  Data corruption?",
                numItems
            );

            if (value == null)
            {
                value = new RingDeque<T>(numItems > 0 ? numItems : RingDeque<T>.DefaultCapacity);
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

        public void Serialize(in RingDeque<T> value, ISerializationWriter writer)
        {
            writer.Write("count", value.Length);

            foreach (var item in value)
            {
                writer.Write("item", item);
            }
        }
    }
}
