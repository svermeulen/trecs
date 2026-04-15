using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class ListSerializer<T> : ISerializer<List<T>>
    {
        public ListSerializer() { }

        public void Deserialize(ref List<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("numItems");
            Assert.That(numItems >= 0);
            Assert.That(
                numItems <= 1000000,
                "Unexpectedly large number of items in list {}.  Data corruption?",
                numItems
            );

            if (value == null)
            {
                value = new List<T>(numItems);
            }
            else
            {
                Assert.That(value.Count == 0);

                value.Clear();

                if (value.Capacity < numItems)
                {
                    value.Capacity = numItems;
                }
            }

            for (int i = 0; i < numItems; i++)
            {
                T item = default;
                reader.Read("item", ref item);
                value.Add(item);
            }
        }

        public void Serialize(in List<T> value, ISerializationWriter writer)
        {
            writer.Write("numItems", value.Count);

            foreach (var item in value)
            {
                writer.Write("item", item);
            }
        }
    }
}
