using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// Single source of truth for the Trecs contiguous wire form:
    /// <c>[16B header][int32 bitFieldBitCount][int32 bitFieldByteCount][int32 dataByteCount][bit-field bytes][data bytes]</c>.
    /// The 16-byte header itself is owned by <see cref="SerializationHeaderUtil"/>; this composes it
    /// with the section-length prefix and the two sections, so the producer (<see cref="SerializationData"/>)
    /// and the read view (<see cref="ContiguousSerializationData"/>) can't drift apart.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class SerializationEnvelope
    {
        /// <summary>The fixed section length prefix: <c>[bitFieldBitCount][bitFieldByteCount][dataByteCount]</c>.</summary>
        public const int SectionPrefixSize = sizeof(int) * 3;

        /// <summary>Total contiguous byte size for sections of the given byte lengths.</summary>
        public static int ContiguousSize(int bitFieldByteCount, int dataByteCount) =>
            SerializationHeaderUtil.Size + SectionPrefixSize + bitFieldByteCount + dataByteCount;

        /// <summary>
        /// Write the full contiguous form into <paramref name="dest"/>, which must be at least
        /// <see cref="ContiguousSize"/> bytes.
        /// </summary>
        public static void Write(
            Span<byte> dest,
            int version,
            long flags,
            bool includeTypeChecks,
            int bitFieldBitCount,
            ReadOnlySpan<byte> bitFields,
            ReadOnlySpan<byte> data
        )
        {
            int offset = 0;
            SerializationHeaderUtil.WriteHeader(
                dest.Slice(offset, SerializationHeaderUtil.Size),
                version,
                flags,
                includeTypeChecks
            );
            offset += SerializationHeaderUtil.Size;

            WritePrefix(
                dest.Slice(offset, SectionPrefixSize),
                bitFieldBitCount,
                bitFields.Length,
                data.Length
            );
            offset += SectionPrefixSize;

            bitFields.CopyTo(dest.Slice(offset));
            offset += bitFields.Length;
            data.CopyTo(dest.Slice(offset));
        }

        /// <summary>Write the full contiguous form to <paramref name="stream"/>.</summary>
        public static void Write(
            Stream stream,
            int version,
            long flags,
            bool includeTypeChecks,
            int bitFieldBitCount,
            ReadOnlySpan<byte> bitFields,
            ReadOnlySpan<byte> data
        )
        {
            Span<byte> head = stackalloc byte[SerializationHeaderUtil.Size + SectionPrefixSize];
            FormatHead(
                head,
                version,
                flags,
                includeTypeChecks,
                bitFieldBitCount,
                bitFields.Length,
                data.Length
            );
            stream.Write(head);
            stream.Write(bitFields);
            stream.Write(data);
        }

        /// <summary>xxHash64 over the full contiguous form, without materializing it.</summary>
        public static ulong Checksum(
            int version,
            long flags,
            bool includeTypeChecks,
            int bitFieldBitCount,
            ReadOnlySpan<byte> bitFields,
            ReadOnlySpan<byte> data
        )
        {
            Span<byte> head = stackalloc byte[SerializationHeaderUtil.Size + SectionPrefixSize];
            FormatHead(
                head,
                version,
                flags,
                includeTypeChecks,
                bitFieldBitCount,
                bitFields.Length,
                data.Length
            );

            var builder = XxHash64Builder.Create();
            builder.Update(head);
            builder.Update(bitFields);
            builder.Update(data);
            return builder.Digest();
        }

        /// <summary>
        /// Parse a contiguous payload: read the header + section prefix, validate the section-length
        /// bounds, and return the header fields plus the <c>(start, length)</c> of each section within
        /// <paramref name="payload"/>. Throws <see cref="SerializationException"/> on a truncated or
        /// corrupt payload. Any bytes after the payload region are ignored.
        /// </summary>
        public static ContiguousLayout Parse(ReadOnlySpan<byte> payload)
        {
            int offset = 0;
            var (version, flags, includeTypeChecks) = SerializationHeaderUtil.ReadHeader(
                payload,
                ref offset
            );

            if (offset + SectionPrefixSize > payload.Length)
            {
                throw new SerializationException(
                    $"Truncated payload — section length prefix runs past end "
                        + $"(offset {offset}, length {payload.Length})."
                );
            }
            int bitFieldBitCount = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset));
            int bitFieldByteCount = BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(offset + sizeof(int))
            );
            int dataByteCount = BinaryPrimitives.ReadInt32LittleEndian(
                payload.Slice(offset + sizeof(int) * 2)
            );
            offset += SectionPrefixSize;

            if (bitFieldByteCount < 0 || dataByteCount < 0)
            {
                throw new SerializationException(
                    $"Corrupt payload — negative section length "
                        + $"(bit-field {bitFieldByteCount}, data {dataByteCount})."
                );
            }

            // Add as long so a corrupt (near-int.MaxValue) count can't overflow the bounds check
            // into a false pass.
            long payloadEnd = (long)offset + bitFieldByteCount + dataByteCount;
            if (payloadEnd > payload.Length)
            {
                throw new SerializationException(
                    $"Truncated payload — sections ({bitFieldByteCount} bit-field + {dataByteCount} "
                        + $"data bytes) run past end (offset {offset}, length {payload.Length})."
                );
            }

            return new ContiguousLayout(
                version,
                flags,
                includeTypeChecks,
                bitFieldBitCount,
                bitFieldStart: offset,
                bitFieldByteCount: bitFieldByteCount,
                dataStart: offset + bitFieldByteCount,
                dataByteCount: dataByteCount
            );
        }

        // Header + section prefix, the fixed 28-byte run at the front of every payload.
        static void FormatHead(
            Span<byte> head,
            int version,
            long flags,
            bool includeTypeChecks,
            int bitFieldBitCount,
            int bitFieldByteCount,
            int dataByteCount
        )
        {
            SerializationHeaderUtil.WriteHeader(
                head.Slice(0, SerializationHeaderUtil.Size),
                version,
                flags,
                includeTypeChecks
            );
            WritePrefix(
                head.Slice(SerializationHeaderUtil.Size),
                bitFieldBitCount,
                bitFieldByteCount,
                dataByteCount
            );
        }

        static void WritePrefix(
            Span<byte> prefix,
            int bitFieldBitCount,
            int bitFieldByteCount,
            int dataByteCount
        )
        {
            BinaryPrimitives.WriteInt32LittleEndian(prefix, bitFieldBitCount);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(sizeof(int)), bitFieldByteCount);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.Slice(sizeof(int) * 2), dataByteCount);
        }
    }

    /// <summary>
    /// Header fields plus the section boundaries (start + length within the payload span) returned by
    /// <see cref="SerializationEnvelope.Parse"/>.
    /// </summary>
    internal readonly struct ContiguousLayout
    {
        public readonly int Version;
        public readonly long Flags;
        public readonly bool IncludeTypeChecks;
        public readonly int BitFieldBitCount;
        public readonly int BitFieldStart;
        public readonly int BitFieldByteCount;
        public readonly int DataStart;
        public readonly int DataByteCount;

        public ContiguousLayout(
            int version,
            long flags,
            bool includeTypeChecks,
            int bitFieldBitCount,
            int bitFieldStart,
            int bitFieldByteCount,
            int dataStart,
            int dataByteCount
        )
        {
            Version = version;
            Flags = flags;
            IncludeTypeChecks = includeTypeChecks;
            BitFieldBitCount = bitFieldBitCount;
            BitFieldStart = bitFieldStart;
            BitFieldByteCount = bitFieldByteCount;
            DataStart = dataStart;
            DataByteCount = dataByteCount;
        }
    }
}
