using System;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// Shared raw-bytes serialization format for NativeBlobBox used by both BlobStoreFiles
    /// and BlobStoreAddressables.
    ///
    /// Format:
    ///   int32 magic (NativeBlobMagic = 'NBLB')
    ///   int32 serializationVersion
    ///   int32 size
    ///   int32 alignment
    ///   [size] bytes
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
            writer.Write(Magic);
            writer.Write(serializationVersion);
            writer.Write(box.Size);
            writer.Write(box.Alignment);

            // Write directly from native memory — no managed byte[] intermediate.
            var span = new ReadOnlySpan<byte>(box.Ptr.ToPointer(), box.Size);
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
            int magic = reader.ReadInt32();
            TrecsAssert.That(
                magic == Magic,
                "Invalid native blob {0}: bad magic {x} (expected {x})",
                sourceDescription,
                magic,
                Magic
            );

            int version = reader.ReadInt32();
            TrecsAssert.That(
                version == expectedSerializationVersion,
                "Native blob serialization version mismatch in {0}: file is {1}, current is {2}",
                sourceDescription,
                version,
                expectedSerializationVersion
            );

            int size = reader.ReadInt32();
            int alignment = reader.ReadInt32();

            TrecsAssert.That(
                size > 0 && size <= MaxBlobSize,
                "Invalid native blob size {0} in {1}",
                size,
                sourceDescription
            );
            TrecsAssert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "Invalid native blob alignment {0} in {1}",
                alignment,
                sourceDescription
            );

            var remaining = stream.Length - stream.Position;
            TrecsAssert.That(
                remaining >= size,
                "Native blob {0} truncated: expected {1} bytes, only {2} available",
                sourceDescription,
                size,
                remaining
            );

            NativeBlobBox box = null;
            try
            {
                box = pool.RentUninitialized(size, alignment, innerType);

                // Read directly into native memory — no managed byte[] intermediate.
                // Stream.Read may return fewer bytes than requested, so loop until full.
                var span = new Span<byte>(box.Ptr.ToPointer(), size);
                int totalRead = 0;
                while (totalRead < size)
                {
                    int bytesRead = stream.Read(span.Slice(totalRead));
                    if (bytesRead == 0)
                    {
                        throw TrecsAssert.CreateException(
                            "Short read on native blob {0}: expected {1} bytes, got {2}",
                            sourceDescription,
                            size,
                            totalRead
                        );
                    }
                    totalRead += bytesRead;
                }
                return box;
            }
            catch
            {
                box?.Dispose();
                throw;
            }
        }
    }
}
