using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for <see cref="FastList{T}"/> of unmanaged elements. Writes
    /// the count followed by the underlying array as a single blit.
    /// </summary>
    public sealed class FastListSerializer<T> : ISerializer<FastList<T>>
        where T : unmanaged
    {
        public FastListSerializer() { }

        public void Deserialize(ref FastList<T> value, ISerializationReader reader)
        {
            var numItems = reader.Read<int>("Count");
            TrecsAssert.That(numItems >= 0);

            if (value == null)
            {
                value = new FastList<T>(numItems);
            }
            else
            {
                TrecsAssert.That(value.Count == 0);

                value.Clear();

                if (value.Capacity < numItems)
                {
                    value.IncreaseCapacityTo(numItems);
                }
            }

            if (numItems == 0)
            {
                return;
            }

            // Set count directly for better performance
            value.SetCountTo(numItems);

            var buffer = value.ToArrayFast(out int count);
            TrecsAssert.That(count == numItems);

            unsafe
            {
                fixed (T* ptr = buffer)
                {
                    reader.BlitReadArrayPtr("Items", ptr, numItems);
                }
            }
        }

        public void Serialize(in FastList<T> value, ISerializationWriter writer)
        {
            writer.Write("Count", value.Count);

            if (value.Count == 0)
            {
                return;
            }

            var buffer = value.ToArrayFast(out int count);

            unsafe
            {
                fixed (T* ptr = buffer)
                {
                    writer.BlitWriteArrayPtr("Items", ptr, count);
                }
            }
        }
    }
}
