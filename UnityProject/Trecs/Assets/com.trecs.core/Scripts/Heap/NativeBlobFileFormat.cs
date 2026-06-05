using System;
using System.IO;
using Unity.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Raw-bytes serialization format for a native (unmanaged) blob — the bytes behind a
    /// <c>NativeSharedPtr&lt;T&gt;</c>. Used by the opaque-blob loaders (Addressables, file store)
    /// and the derivable output-cache (<c>DiskMemoize</c>) to round-trip a native blob
    /// through a byte stream with no managed <c>byte[]</c> intermediate. Both a pooled
    /// <see cref="NativeBlobBox"/> and a raw <see cref="NativeBlobAllocation"/> (the taking-ownership
    /// shape) can be written and read back.
    /// <para>
    /// Format:
    /// <code>
    ///   int32 magic  (NativeBlobMagic = 'NBLB')
    ///   int32 serializationVersion
    ///   int32 size
    ///   int32 alignment
    ///   [size] bytes
    /// </code>
    /// </para>
    /// </summary>
    internal static class NativeBlobFileFormat
    {
        public const int Magic = 0x4E424C42; // "NBLB"
        public const int MaxBlobSize = 256 * 1024 * 1024; // 256 MB sanity cap

        public static unsafe void Write(
            BinaryWriter writer,
            int serializationVersion,
            NativeBlobBox box
        )
        {
            WriteCore(writer, serializationVersion, box.Ptr, box.Size, box.Alignment);
        }

        /// <summary>Writes a raw <see cref="NativeBlobAllocation"/> (taking-ownership shape).</summary>
        public static unsafe void WriteAllocation(
            BinaryWriter writer,
            int serializationVersion,
            NativeBlobAllocation alloc
        )
        {
            WriteCore(writer, serializationVersion, alloc.Ptr, alloc.AllocSize, alloc.Alignment);
        }

        static unsafe void WriteCore(
            BinaryWriter writer,
            int serializationVersion,
            IntPtr ptr,
            int size,
            int alignment
        )
        {
            writer.Write(Magic);
            writer.Write(serializationVersion);
            writer.Write(size);
            writer.Write(alignment);

            // Write directly from native memory — no managed byte[] intermediate.
            var span = new ReadOnlySpan<byte>(ptr.ToPointer(), size);
            writer.BaseStream.Write(span);
        }

        public static unsafe NativeBlobBox Read(
            BinaryReader reader,
            Stream stream,
            int expectedSerializationVersion,
            Type innerType,
            string sourceDescription,
            NativeBlobBoxPool pool
        )
        {
            ReadHeader(
                reader,
                stream,
                expectedSerializationVersion,
                sourceDescription,
                out int size,
                out int alignment
            );

            NativeBlobBox box = null;
            try
            {
                box = pool.RentUninitialized(size, alignment, innerType);
                ReadBytesInto(stream, box.Ptr, size, sourceDescription);
                return box;
            }
            catch
            {
                box?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Reads a native blob into a freshly-allocated <see cref="Allocator.Persistent"/> buffer and
        /// returns it as a <see cref="NativeBlobAllocation"/> — for the taking-ownership path, where
        /// the cache (not a pool) takes ownership and frees it via <c>AllocatorManager</c>.
        /// </summary>
        public static unsafe NativeBlobAllocation ReadAllocation(
            BinaryReader reader,
            Stream stream,
            int expectedSerializationVersion,
            string sourceDescription
        )
        {
            ReadHeader(
                reader,
                stream,
                expectedSerializationVersion,
                sourceDescription,
                out int size,
                out int alignment
            );

            var ptr = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, items: 1);
            try
            {
                ReadBytesInto(stream, (IntPtr)ptr, size, sourceDescription);
                return new NativeBlobAllocation((IntPtr)ptr, size, alignment);
            }
            catch
            {
                AllocatorManager.Free(Allocator.Persistent, ptr, size, alignment, items: 1);
                throw;
            }
        }

        static void ReadHeader(
            BinaryReader reader,
            Stream stream,
            int expectedSerializationVersion,
            string sourceDescription,
            out int size,
            out int alignment
        )
        {
            int magic = reader.ReadInt32();
            TrecsDebugAssert.That(
                magic == Magic,
                "Invalid native blob {0}: bad magic {1} (expected {2})",
                sourceDescription,
                magic,
                Magic
            );

            int version = reader.ReadInt32();
            TrecsDebugAssert.That(
                version == expectedSerializationVersion,
                "Native blob serialization version mismatch in {0}: file is {1}, current is {2}",
                sourceDescription,
                version,
                expectedSerializationVersion
            );

            size = reader.ReadInt32();
            alignment = reader.ReadInt32();

            TrecsDebugAssert.That(
                size > 0 && size <= MaxBlobSize,
                "Invalid native blob size {0} in {1}",
                size,
                sourceDescription
            );
            TrecsDebugAssert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "Invalid native blob alignment {0} in {1}",
                alignment,
                sourceDescription
            );

            var remaining = stream.Length - stream.Position;
            TrecsDebugAssert.That(
                remaining >= size,
                "Native blob {0} truncated: expected {1} bytes, only {2} available",
                sourceDescription,
                size,
                remaining
            );
        }

        static unsafe void ReadBytesInto(
            Stream stream,
            IntPtr destination,
            int size,
            string sourceDescription
        )
        {
            // Read directly into native memory — no managed byte[] intermediate. Stream.Read may
            // return fewer bytes than requested, so loop until full.
            var span = new Span<byte>(destination.ToPointer(), size);
            int totalRead = 0;
            while (totalRead < size)
            {
                int bytesRead = stream.Read(span.Slice(totalRead));
                if (bytesRead == 0)
                {
                    throw TrecsDebugAssert.CreateException(
                        "Short read on native blob {0}: expected {1} bytes, got {2}",
                        sourceDescription,
                        size,
                        totalRead
                    );
                }
                totalRead += bytesRead;
            }
        }
    }
}
