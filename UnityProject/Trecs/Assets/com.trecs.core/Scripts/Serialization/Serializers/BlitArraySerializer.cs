using System;
using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for arrays of unmanaged elements. Writes the length
    /// followed by the elements as a single blit, avoiding the per-element
    /// name/type framing that <see cref="ArraySerializer{TElem}"/> incurs.
    /// </summary>
    public sealed class BlitArraySerializer<TElem> : ISerializer<TElem[]>
        where TElem : unmanaged
    {
        public BlitArraySerializer() { }

        public void Serialize(in TElem[] value, ISerializationWriter writer)
        {
            TrecsAssert.IsNotNull(value);

            writer.Write("Count", value.Length);
            writer.BlitWriteArray("Value", value, value.Length);
        }

        public void Deserialize(ref TElem[] value, ISerializationReader reader)
        {
            var length = reader.Read<int>("Count");
            TrecsAssert.That(length >= 0);

            if (length == 0)
            {
                value = Array.Empty<TElem>();
                return;
            }

            if (value == null || value.Length != length)
            {
                value = new TElem[length];
            }

            reader.BlitReadArray("Value", value, length);
        }
    }
}
