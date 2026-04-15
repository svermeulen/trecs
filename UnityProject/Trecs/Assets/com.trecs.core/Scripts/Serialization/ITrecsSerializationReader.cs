using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface ITrecsSerializationReader
    {
        void Read<T>(string name, ref T value);
        void ReadObject(string name, ref object value);
        unsafe void BlitReadRawBytes(string name, void* ptr, int numBytes);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class TrecsSerializationReaderExtensions
    {
        public static T Read<T>(this ITrecsSerializationReader reader, string name)
        {
            T value = default;
            reader.Read(name, ref value);
            return value;
        }

        public static void ReadInPlace<T>(
            this ITrecsSerializationReader reader,
            string name,
            T value
        )
            where T : class
        {
            Assert.That(value != null);
            T tempValue = value;
            reader.Read(name, ref tempValue);
            Assert.That(ReferenceEquals(tempValue, value));
        }

        public static object ReadObject(this ITrecsSerializationReader reader, string name)
        {
            object value = null;
            reader.ReadObject(name, ref value);
            return value;
        }

        public static void ReadObjectInPlace(
            this ITrecsSerializationReader reader,
            string name,
            object value
        )
        {
            Assert.That(value != null);
            object tempValue = value;
            reader.ReadObject(name, ref tempValue);
            Assert.That(ReferenceEquals(tempValue, value));
        }
    }
}
