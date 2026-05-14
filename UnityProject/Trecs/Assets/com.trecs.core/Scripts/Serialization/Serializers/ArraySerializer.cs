using System;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for arrays of managed/custom elements. Writes the length
    /// followed by each element via the registered serializer for
    /// <typeparamref name="TElem"/>. For arrays of unmanaged types use
    /// <see cref="BlitArraySerializer{TElem}"/> instead, which writes the
    /// elements as a single blit and avoids per-element framing.
    /// </summary>
    public sealed class ArraySerializer<TElem> : ISerializer<TElem[]>
        where TElem : class
    {
        public ArraySerializer() { }

        public void Serialize(in TElem[] value, ISerializationWriter writer)
        {
            TrecsAssert.IsNotNull(value);

            writer.Write("Count", value.Length);

            for (int i = 0; i < value.Length; i++)
            {
                writer.Write("Item", value[i]);
            }
        }

        public void Deserialize(ref TElem[] value, ISerializationReader reader)
        {
            var length = reader.Read<int>("Count");

            if (length == 0)
            {
                value = Array.Empty<TElem>();
                return;
            }

            if (value == null || value.Length != length)
            {
                value = new TElem[length];
            }

            for (int i = 0; i < length; i++)
            {
                TElem item = default;
                reader.Read("Item", ref item);
                value[i] = item;
            }
        }
    }
}
