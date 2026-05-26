using System;
using System.Buffers;
using System.ComponentModel;
using System.IO;

namespace Trecs.Internal
{
    // Main-thread only — every public method asserts
    // UnityThreadHelper.IsMainThread. The class itself holds no shared
    // mutable state (everything flows through the caller-supplied
    // ArrayBufferWriter / MemoryStream), so the contract is enforced at
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

        // Direct-from-MemoryStream reads — symmetric counterpart to the
        // ArrayBufferWriter-targeted writes above. BinaryReader-based reads
        // walk the BinaryReader.Read(byte[], int, int) → Stream.Read chain
        // (two virtual dispatches in IL2CPP) plus a memcpy through a static
        // staging buffer. The MemoryStream overload skips both — it reads
        // the underlying byte[] directly via GetBuffer() and advances
        // Position manually, so subsequent BinaryReader.ReadString /
        // ReadInt32 calls on the same stream still find the right offset.
        public static unsafe void ReadRaw(void* valuePtr, int numBytes, MemoryStream stream)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            int pos = (int)stream.Position;
            // Unconditional throw: a short read on a blit path silently blits
            // stale buffer content into the caller's memory, which corrupts
            // components rather than failing. Must trip in release too.
            if (pos + numBytes > stream.Length)
            {
                throw new SerializationException(
                    $"Truncated stream: expected {numBytes} bytes at position {pos} but stream length is only {stream.Length}"
                );
            }

            if (stream.TryGetBuffer(out ArraySegment<byte> seg))
            {
                // Resizable / publicly-visible MemoryStream — read straight
                // from the internal buffer. No virtual dispatch.
                fixed (byte* bufferPtr = &seg.Array[seg.Offset + pos])
                {
                    Buffer.MemoryCopy(bufferPtr, valuePtr, numBytes, numBytes);
                }
                stream.Position = pos + numBytes;
            }
            else
            {
                // Fallback for `new MemoryStream(byte[])` and friends, where
                // GetBuffer is not accessible. Still cheaper than the old
                // BinaryReader.Read → Stream.Read(byte[], …) chain — this is
                // one virtual call on MemoryStream.Read(Span<byte>) and the
                // copy goes straight to the caller's buffer.
                int bytesRead = stream.Read(new Span<byte>(valuePtr, numBytes));
                if (bytesRead != numBytes)
                {
                    throw new SerializationException(
                        $"Truncated stream: expected {numBytes} bytes, got {bytesRead} at position {pos}"
                    );
                }
            }
        }

        public static unsafe void Read<T>(ref T value, MemoryStream stream)
            where T : unmanaged
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            fixed (void* valuePtr = &value)
            {
                ReadRaw(valuePtr, sizeof(T), stream);
            }
        }

        public static unsafe void ReadArray<T>(T[] value, int count, MemoryStream stream)
            where T : unmanaged
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            TrecsDebugAssert.That(value.Length >= count);

            fixed (void* valuePtr = value)
            {
                ReadRaw(valuePtr, sizeof(T) * count, stream);
            }
        }

        public static unsafe void ReadArrayPtr<T>(T* valuePtr, int length, MemoryStream stream)
            where T : unmanaged
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            ReadRaw(valuePtr, sizeof(T) * length, stream);
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
