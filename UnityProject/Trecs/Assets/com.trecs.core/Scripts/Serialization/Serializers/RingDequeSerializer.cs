using Trecs.Internal;

namespace Trecs.Serialization
{
    public sealed class RingDequeSerializer<T> : ISerializer<RingDeque<T>>
    {
        public RingDequeSerializer() { }

        public void Deserialize(ref RingDeque<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("Count");
            TrecsAssert.That(numItems >= 0);
            TrecsAssert.That(
                numItems <= 1000000,
                "Unexpectedly large number of items in ring deque ({0}).  Data corruption?",
                numItems
            );

            if (value == null)
            {
                value = new RingDeque<T>(numItems > 0 ? numItems : RingDeque<T>.DefaultCapacity);
            }
            else
            {
                TrecsAssert.That(value.IsEmpty);
                value.Clear();
                value.EnsureCapacity(numItems);
            }

            for (int i = 0; i < numItems; i++)
            {
                T item = default;
                reader.Read("Item", ref item);
                value.PushBack(item);
            }
        }

        public void Serialize(in RingDeque<T> value, ISerializationWriter writer)
        {
            writer.Write("Count", value.Length);

            foreach (var item in value)
            {
                writer.Write("Item", item);
            }
        }
    }
}
