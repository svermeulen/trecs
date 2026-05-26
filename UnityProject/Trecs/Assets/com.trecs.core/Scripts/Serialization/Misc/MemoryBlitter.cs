using System;
using System.Buffers;
using System.ComponentModel;

namespace Trecs.Internal
{
    // Main-thread only — every public method asserts
    // UnityThreadHelper.IsMainThread. The class itself holds no shared
    // mutable state (everything flows through the caller-supplied
    // ArrayBufferWriter / ReadOnlySpan), so the contract is enforced at
    // call boundaries rather than by locking.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class MemoryBlitter
    {
        static MemoryBlitter()
        {
            // Trecs serializes via direct memory blits (Buffer.MemoryCopy), so the
            // wire format is little-endian-on-disk by virtue of every supported
            // Unity target being LE. If Unity ever ships a BE target, payloads
            // saved on one would not load on the other — fail fast at startup.
            TrecsDebugAssert.That(
                BitConverter.IsLittleEndian,
                "Trecs serialization assumes a little-endian platform"
            );
        }

        public static unsafe void ReadRaw(
            void* valuePtr,
            int numBytes,
            ReadOnlySpan<byte> data,
            ref int offset
        )
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            if (offset + numBytes > data.Length)
            {
                throw new SerializationException(
                    $"Truncated data: expected {numBytes} bytes at offset {offset} but data length is only {data.Length}"
                );
            }

            fixed (byte* srcPtr = &data[offset])
            {
                Buffer.MemoryCopy(srcPtr, valuePtr, numBytes, numBytes);
            }
            offset += numBytes;
        }

        public static unsafe void Read<T>(ref T value, ReadOnlySpan<byte> data, ref int offset)
            where T : unmanaged
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            fixed (void* valuePtr = &value)
            {
                ReadRaw(valuePtr, sizeof(T), data, ref offset);
            }
        }

        public static unsafe void ReadArray<T>(
            T[] value,
            int count,
            ReadOnlySpan<byte> data,
            ref int offset
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            TrecsDebugAssert.That(value.Length >= count);

            fixed (void* valuePtr = value)
            {
                ReadRaw(valuePtr, sizeof(T) * count, data, ref offset);
            }
        }

        public static unsafe void ReadArrayPtr<T>(
            T* valuePtr,
            int length,
            ReadOnlySpan<byte> data,
            ref int offset
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            ReadRaw(valuePtr, sizeof(T) * length, data, ref offset);
        }

        // Direct-to-buffer writes used by BinarySerializationWriter's blit path.
        // Takes the concrete ArrayBufferWriter<byte> so GetSpan/Advance are
        // direct (non-virtual) calls — important on IL2CPP for the blit hot
        // path (~tens of thousands of calls per Save at 10k entities).
        public static unsafe void Write<T>(in T value, ArrayBufferWriter<byte> bufferWriter)
            where T : unmanaged
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            fixed (void* valuePtr = &value)
            {
                WriteRaw(valuePtr, sizeof(T), bufferWriter);
            }
        }

        public static unsafe void WriteRaw(
            void* valuePtr,
            int numBytes,
            ArrayBufferWriter<byte> bufferWriter
        )
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            var span = bufferWriter.GetSpan(numBytes);
            fixed (byte* destPtr = span)
            {
                Buffer.MemoryCopy(valuePtr, destPtr, numBytes, numBytes);
            }
            bufferWriter.Advance(numBytes);
        }

        public static unsafe void WriteArray<T>(
            T[] value,
            int count,
            ArrayBufferWriter<byte> bufferWriter
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(value.Length >= count);
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            fixed (void* valuePtr = value)
            {
                WriteRaw(valuePtr, sizeof(T) * count, bufferWriter);
            }
        }

        public static unsafe void WriteArrayPtr<T>(
            T* valuePtr,
            int length,
            ArrayBufferWriter<byte> bufferWriter
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            WriteRaw(valuePtr, sizeof(T) * length, bufferWriter);
        }
    }
}
