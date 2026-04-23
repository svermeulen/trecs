using System;
using System.ComponentModel;
using System.IO;
using Trecs.Serialization;

namespace Trecs.Internal
{
    // NOTE: Not thread safe
    // If needed then use thread locals instead
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class MemoryBlitter
    {
        static byte[] _buffer;

        static void EnsureBufferCapacity(int requiredCapacity)
        {
            if (_buffer == null)
            {
                _buffer = new byte[requiredCapacity];
                return;
            }

            if (_buffer.Length < requiredCapacity)
            {
                int newCapacity = Math.Max(_buffer.Length * 2, requiredCapacity);
                Array.Resize(ref _buffer, newCapacity);
            }
        }

        public static unsafe void ReadRaw(void* valuePtr, int numBytes, BinaryReader reader)
        {
            EnsureBufferCapacity(numBytes);

            int bytesRead = reader.Read(_buffer, 0, numBytes);

            // Unconditional throw: a short read on a blit path silently blits
            // stale buffer content into the caller's memory, which corrupts
            // components rather than failing. Must trip in release too.
            if (bytesRead != numBytes)
            {
                throw new SerializationException(
                    $"Truncated stream: expected {numBytes} bytes, got {bytesRead} at position {reader.BaseStream.Position}"
                );
            }

            fixed (void* bufferPtr = _buffer)
            {
                Buffer.MemoryCopy(bufferPtr, valuePtr, numBytes, numBytes);
            }
        }

        public static unsafe void Read<T>(ref T value, BinaryReader reader)
            where T : unmanaged
        {
            Assert.That(UnityThreadHelper.IsMainThread);

            fixed (void* valuePtr = &value)
            {
                ReadRaw(valuePtr, sizeof(T), reader);
            }
        }

        public static unsafe void ReadArray<T>(T[] value, BinaryReader reader)
            where T : unmanaged
        {
            ReadArray(value, value.Length, reader);
        }

        public static unsafe void ReadArray<T>(T[] value, int count, BinaryReader reader)
            where T : unmanaged
        {
            Assert.That(UnityThreadHelper.IsMainThread);

            Assert.That(value.Length >= count);

            fixed (void* valuePtr = value)
            {
                ReadRaw(valuePtr, sizeof(T) * count, reader);
            }
        }

        public static unsafe void ReadArrayPtr<T>(T* valuePtr, int length, BinaryReader reader)
            where T : unmanaged
        {
            Assert.That(UnityThreadHelper.IsMainThread);

            ReadRaw(valuePtr, sizeof(T) * length, reader);
        }

        public static unsafe void Write<T>(in T value, BinaryWriter writer)
            where T : unmanaged
        {
            Assert.That(UnityThreadHelper.IsMainThread);

            fixed (void* valuePtr = &value)
            {
                WriteRaw(valuePtr, sizeof(T), writer);
            }
        }

        public static unsafe void WriteRaw(void* valuePtr, int numBytes, BinaryWriter writer)
        {
            Assert.That(UnityThreadHelper.IsMainThread);
            EnsureBufferCapacity(numBytes);

            fixed (void* bufferPtr = _buffer)
            {
                Buffer.MemoryCopy(valuePtr, bufferPtr, numBytes, numBytes);
            }

            writer.Write(_buffer, 0, numBytes);
        }

        public static unsafe void WriteArray<T>(T[] value, BinaryWriter writer)
            where T : unmanaged
        {
            WriteArray(value, value.Length, writer);
        }

        public static unsafe void WriteArray<T>(T[] value, int count, BinaryWriter writer)
            where T : unmanaged
        {
            Assert.That(value.Length >= count);
            Assert.That(UnityThreadHelper.IsMainThread);

            fixed (void* valuePtr = value)
            {
                WriteRaw(valuePtr, sizeof(T) * count, writer);
            }
        }

        public static unsafe void WriteArrayPtr<T>(T* valuePtr, int length, BinaryWriter writer)
            where T : unmanaged
        {
            Assert.That(UnityThreadHelper.IsMainThread);

            WriteRaw(valuePtr, sizeof(T) * length, writer);
        }
    }
}
