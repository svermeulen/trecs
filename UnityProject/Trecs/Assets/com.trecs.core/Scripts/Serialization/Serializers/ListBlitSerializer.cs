using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public sealed class ListBlitSerializer<T> : ISerializer<List<T>>
        where T : unmanaged
    {
        public ListBlitSerializer() { }

        public void Deserialize(ref List<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("Count");
            TrecsDebugAssert.That(numItems >= 0);

            if (value == null)
            {
                value = new List<T>(numItems);
            }
            else
            {
                TrecsDebugAssert.That(value.Count == 0);
                value.Clear();

                if (value.Capacity < numItems)
                {
                    value.Capacity = numItems;
                }
            }

            if (numItems == 0)
            {
                return;
            }

            var buffer = new T[numItems];

            unsafe
            {
                fixed (T* ptr = buffer)
                {
                    reader.BlitReadArrayPtr("Items", ptr, numItems);
                }
            }

            value.AddRange(buffer);
        }

        public void Serialize(in List<T> value, ISerializationWriter writer)
        {
            writer.Write("Count", value.Count);

            if (value.Count == 0)
            {
                return;
            }

            var buffer = new T[value.Count];
            value.CopyTo(buffer);

            unsafe
            {
                fixed (T* ptr = buffer)
                {
                    writer.BlitWriteArrayPtr("Items", ptr, value.Count);
                }
            }
        }
    }
}
