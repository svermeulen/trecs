using Trecs.Internal;

namespace Trecs.Serialization
{
    /// <summary>
    /// Serializer for typed arrays (<typeparamref name="T"/> is an array type,
    /// <typeparamref name="TElem"/> its element type). Writes the length
    /// followed by each element via the registered serializer for
    /// <typeparamref name="TElem"/>. For arrays of unmanaged types prefer the
    /// blit path — this serializer is for arrays of managed/custom elements.
    /// </summary>
    public class ArraySerializer<T, TElem> : ISerializer<T>
    {
        static readonly TrecsLog _log = new("ArraySerializer");

        public ArraySerializer() { }

        public void Serialize(in T value, ISerializationWriter writer)
        {
            var valueArray = (TElem[])(object)value;

            Assert.IsNotNull(valueArray);

            writer.Write("length", valueArray.Length);

            for (int i = 0; i < valueArray.Length; i++)
            {
                writer.Write("item", valueArray[i]);
            }
        }

        public void Deserialize(ref T value, ISerializationReader reader)
        {
            var length = reader.Read<int>("length");
            if (length < 0 || length > SerializationConstants.MaxCollectionLength)
            {
                throw new SerializationException(
                    $"Invalid {typeof(TElem).Name}[] length {length} (max {SerializationConstants.MaxCollectionLength}) — truncated or corrupt stream"
                );
            }

            if (length == 0)
            {
                value = (T)(object)new TElem[0];
                return;
            }

            var array = new TElem[length];

            for (int i = 0; i < length; i++)
            {
                TElem item = default;
                reader.Read("item", ref item);
                array[i] = item;
            }

            value = (T)(object)array;
        }
    }
}
