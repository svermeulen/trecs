using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="Queue{T}"/>. Writes the item count followed
    /// by each element in FIFO order via the registered serializer for
    /// <typeparamref name="T"/>.
    /// </summary>
    public sealed class QueueSerializer<T> : ISerializer<Queue<T>>
    {
        public QueueSerializer() { }

        public void Deserialize(ref Queue<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("Count");
            TrecsDebugAssert.That(numItems >= 0);

            // Queue<T> in Unity's .NET Standard 2.1 surface has no
            // EnsureCapacity (added in .NET 5); pre-size via the constructor
            // when allocating fresh, otherwise reuse the existing backing
            // array.
            if (value == null)
            {
                value = new Queue<T>(numItems);
            }
            else
            {
                TrecsDebugAssert.That(value.Count == 0);
                value.Clear();
            }

            for (int i = 0; i < numItems; i++)
            {
                T item = default;
                reader.Read("Item", ref item);
                value.Enqueue(item);
            }
        }

        public void Serialize(in Queue<T> value, ISerializationWriter writer)
        {
            writer.Write("Count", value.Count);

            foreach (var item in value)
            {
                writer.Write("Item", item);
            }
        }
    }
}
