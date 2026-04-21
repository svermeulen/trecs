using System;

namespace Trecs.Serialization
{
    public interface ISerializationWriter
    {
        int Version { get; }

        long NumBytesWritten { get; }

        long Flags { get; }

        bool HasFlag(long flag);

        void WriteBit(bool value);

        /// <summary>
        /// Writes a concrete type value. Use this when the exact type is known at compile time.
        /// The type information is optionally written for verification during deserialization.
        /// </summary>
        void Write<T>(string name, in T value);

        /// <summary>
        /// Writes an abstract type (interface/base class) value.
        /// Stores the concrete type ID in the stream for polymorphic serialization.
        /// Use this when serializing through base classes or interfaces.
        /// </summary>
        void WriteObject(string name, object value);

        /// <summary>
        /// Writes a string value to the stream.
        /// </summary>
        void WriteString(string name, string value);

        /// <summary>
        /// Writes an unmanaged (blittable) value using direct memory copying.
        /// Maximum performance for primitive types and simple structs.
        /// </summary>
        void BlitWrite<T>(string name, in T value)
            where T : unmanaged;

        /// <summary>
        /// Writes from a pointer location for unmanaged values using direct memory copying.
        /// Unsafe operation for maximum performance scenarios.
        /// </summary>
        unsafe void BlitWriteArrayPtr<T>(string name, T* value, int length)
            where T : unmanaged;

        /// <summary>
        /// Writes an array of unmanaged values using direct memory copying.
        /// Maximum performance for arrays of primitive types.
        /// </summary>
        unsafe void BlitWriteArray<T>(string name, T[] value, int count)
            where T : unmanaged;

        // Shouldn't need to use this except very low level stuff
        void WriteTypeId(string name, Type type);

        /// <summary>
        /// Writes a concrete type value with delta compression.
        /// Uses IEquatable<T>.Equals for equality comparison. If values are equal,
        /// only a single bit flag is written. If different, the full serialized data is written.
        /// Use this when the exact type is known at compile time.
        /// </summary>
        void WriteDelta<T>(string name, in T value, in T baseValue);

        /// <summary>
        /// Writes an abstract type (interface/base class) value with delta compression.
        /// Stores the concrete type ID in the stream for polymorphic serialization.
        /// Uses IEquatable<T>.Equals for equality comparison. If values are equal,
        /// only a single bit flag is written. If different, the type ID and serialized data are written.
        /// Use this when serializing through base classes or interfaces.
        /// </summary>
        void WriteObjectDelta(string name, object value, object baseValue);

        /// <summary>
        /// Writes a string value with delta compression.
        /// Uses reference equality comparison for strings.
        /// </summary>
        void WriteStringDelta(string name, string value, string baseValue);

        /// <summary>
        /// Writes an unmanaged (blittable) value with delta compression.
        /// Uses binary equality comparison and direct memory copying for maximum performance.
        /// </summary>
        void BlitWriteDelta<T>(string name, in T value, in T baseValue)
            where T : unmanaged;

        /// <summary>
        /// Writes a length-prefixed byte range from <paramref name="buffer"/>.
        /// For JSON serialization, bytes are encoded as base64.
        /// For binary serialization, writes an int length followed by the bytes.
        /// </summary>
        void WriteBytes(string name, byte[] buffer, int offset, int count);

        /// <summary>
        /// Writes raw bytes from a pointer using direct memory copying.
        /// Non-generic version for type-erased blitting scenarios.
        /// </summary>
        unsafe void BlitWriteRawBytes(string name, void* ptr, int numBytes);
    }
}
