using System;
using Trecs.Internal;

namespace Trecs
{
    public interface ISerializationReader
    {
        int Version { get; }

        long Flags { get; }

        bool HasFlag(long flag);

        /// <summary>
        /// Reads an unmanaged (blittable) value using direct memory copying.
        /// Maximum performance for primitive types and simple structs.
        /// </summary>
        void BlitRead<T>(string name, ref T value)
            where T : unmanaged;

        /// <summary>
        /// Reads an array of unmanaged values using direct memory copying.
        /// Maximum performance for arrays of primitive types.
        /// </summary>
        unsafe void BlitReadArray<T>(string name, T[] buffer, int count)
            where T : unmanaged;

        /// <summary>
        /// Reads to a pointer location for unmanaged values using direct memory copying.
        /// Unsafe operation for maximum performance scenarios.
        /// </summary>
        unsafe void BlitReadArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged;

        bool ReadBit();

        /// <summary>
        /// Reads a concrete type value. Use this when the exact type is known at compile time.
        /// The type information is verified during deserialization if type checking is enabled.
        /// If <typeparamref name="T"/> is abstract (interface or abstract class), the call is
        /// dispatched to <see cref="ReadObject(string, ref object)"/>, which always reads the
        /// concrete type ID regardless of the type-checking setting.
        /// </summary>
        void Read<T>(string name, ref T value);

        /// <summary>
        /// Reads an abstract type (interface/base class) value.
        /// The concrete type ID is read from the stream, allowing polymorphic deserialization.
        /// Use this when deserializing through base classes or interfaces.
        /// </summary>
        void ReadObject(string name, ref object value);

        /// <summary>
        /// Reads a string value from the stream.
        /// </summary>
        string ReadString(string name);

        // Shouldn't need to use this except very low level stuff
        Type ReadTypeId(string name);

        /// <summary>
        /// Reads an unmanaged (blittable) value that was serialized with delta compression.
        /// Uses binary equality comparison and direct memory copying for maximum performance.
        /// </summary>
        void BlitReadDelta<T>(string name, ref T value, in T baseValue)
            where T : unmanaged;

        /// <summary>
        /// Reads a concrete type value that was serialized with delta compression.
        /// Uses IEquatable<T>.Equals for equality comparison. For reference types,
        /// requires the type to be registered with PoolManager for object cloning.
        /// Use this when the exact type is known at compile time.
        /// </summary>
        void ReadDelta<T>(string name, ref T value, in T baseValue);

        /// <summary>
        /// Reads an abstract type (interface/base class) value that was serialized with delta compression.
        /// The concrete type ID is stored in the stream, allowing polymorphic deserialization.
        /// Uses IEquatable<T>.Equals for equality comparison. For reference types,
        /// requires the concrete type to be registered with PoolManager for object cloning.
        /// Use this when deserializing through base classes or interfaces.
        /// </summary>
        void ReadObjectDelta(string name, ref object value, object baseValue);

        /// <summary>
        /// Reads a string value that was serialized with delta compression.
        /// Uses reference equality comparison for strings.
        /// </summary>
        string ReadStringDelta(string name, string baseValue);

        /// <summary>
        /// Reads a length-prefixed byte range into <paramref name="buffer"/>,
        /// resizing it if it's too small to hold the payload. Returns the
        /// actual number of bytes read (which may be less than buffer.Length).
        /// </summary>
        int ReadBytes(string name, ref byte[] buffer);

        /// <summary>
        /// Reads raw bytes into a pointer using direct memory copying.
        /// Non-generic version for type-erased blitting scenarios.
        /// </summary>
        unsafe void BlitReadRawBytes(string name, void* ptr, int numBytes);
    }

    public static class SerializationReaderExtensions
    {
        public static T Read<T>(this ISerializationReader reader, string name)
        {
            T value = default;
            reader.Read(name, ref value);
            return value;
        }

        public static void ReadInPlace<T>(this ISerializationReader reader, string name, T value)
            where T : class
        {
            TrecsAssert.That(value != null);
            T tempValue = value;
            reader.Read(name, ref tempValue);
            TrecsAssert.That(ReferenceEquals(tempValue, value));
        }

        public static object ReadObject(this ISerializationReader reader, string name)
        {
            object value = null;
            reader.ReadObject(name, ref value);
            return value;
        }

        public static void ReadObjectInPlace(
            this ISerializationReader reader,
            string name,
            object value
        )
        {
            TrecsAssert.That(value != null);
            object tempValue = value;
            reader.ReadObject(name, ref tempValue);
            TrecsAssert.That(ReferenceEquals(tempValue, value));
        }

        public static T ReadDelta<T>(this ISerializationReader reader, string name, in T baseValue)
        {
            T value = default;
            reader.ReadDelta(name, ref value, baseValue);
            return value;
        }

        public static void ReadInPlaceDelta<T>(
            this ISerializationReader reader,
            string name,
            T value,
            T baseValue
        )
        {
            TrecsAssert.That(value != null);
            T tempValue = value;
            reader.ReadDelta(name, ref tempValue, baseValue);
            TrecsAssert.That(ReferenceEquals(tempValue, value));
        }

        public static object ReadObjectDelta(
            this ISerializationReader reader,
            string name,
            object baseValue
        )
        {
            object value = null;
            reader.ReadObjectDelta(name, ref value, baseValue);
            return value;
        }

        public static void ReadObjectInPlaceDelta(
            this ISerializationReader reader,
            string name,
            object value,
            object baseValue
        )
        {
            TrecsAssert.That(value != null);
            object tempValue = value;
            reader.ReadObjectDelta(name, ref tempValue, baseValue);

            TrecsAssert.That(ReferenceEquals(tempValue, value));
        }
    }
}
