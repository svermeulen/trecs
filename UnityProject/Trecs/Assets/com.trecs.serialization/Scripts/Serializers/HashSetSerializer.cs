using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="HashSet{T}"/>. Writes element count followed
    /// by each element via the registered serializer for <typeparamref name="T"/>.
    /// Order is not preserved (managed HashSet does not maintain insertion order).
    /// </summary>
    public sealed class HashSetSerializer<T> : ISerializer<HashSet<T>>
    {
        public HashSetSerializer() { }

        public void Deserialize(ref HashSet<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("count");
            Assert.That(numItems >= 0);

            if (value == null)
            {
                value = new HashSet<T>();
            }
            else
            {
                Assert.That(value.Count == 0);

                value.Clear();
                value.EnsureCapacity(numItems);
            }

            for (int i = 0; i < numItems; i++)
            {
                T item = default;
                reader.Read("item", ref item);
                value.Add(item);
            }
        }

        public void Serialize(in HashSet<T> value, ISerializationWriter writer)
        {
            writer.Write("count", value.Count);

            foreach (var item in value)
            {
                writer.Write("item", item);
            }
        }
    }
}
