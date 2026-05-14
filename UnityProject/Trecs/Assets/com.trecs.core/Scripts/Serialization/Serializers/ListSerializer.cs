using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="List{T}"/>. Writes the item count followed by
    /// each element via the registered serializer for <typeparamref name="T"/>.
    /// </summary>
    public sealed class ListSerializer<T> : ISerializer<List<T>>
    {
        public ListSerializer() { }

        public void Deserialize(ref List<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("Count");

            if (value == null)
            {
                value = new List<T>(numItems);
            }
            else
            {
                TrecsAssert.That(value.Count == 0);

                value.Clear();

                if (value.Capacity < numItems)
                {
                    value.Capacity = numItems;
                }
            }

            for (int i = 0; i < numItems; i++)
            {
                T item = default;
                reader.Read("Item", ref item);
                value.Add(item);
            }
        }

        public void Serialize(in List<T> value, ISerializationWriter writer)
        {
            writer.Write("Count", value.Count);

            foreach (var item in value)
            {
                writer.Write("Item", item);
            }
        }
    }
}
