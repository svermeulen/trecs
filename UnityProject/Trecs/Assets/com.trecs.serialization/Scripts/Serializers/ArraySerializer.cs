using Trecs.Internal;

namespace Trecs.Serialization
{
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
            Assert.That(length >= 0);
            Assert.That(
                length <= 1000000,
                "Unexpectedly large array length {}.  Data corruption?",
                length
            );

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
