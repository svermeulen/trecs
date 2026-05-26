using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class SerializationHeaderUtil
    {
        // Magic bytes at the start of every Trecs-serialized payload ("TR" in ASCII).
        // Readers check these to fail loudly on unrelated/corrupt streams.
        public const byte MagicByte0 = (byte)'T';
        public const byte MagicByte1 = (byte)'R';

        // Bump when the header layout changes so mixed-version code fails loudly
        // rather than silently misparsing.
        //   0: legacy (int version + bool includeTypeChecks)
        //   1: adds long flags between version and includeTypeChecks; prepends
        //      magic bytes ('T','R') + format version byte.
        //   2: data section drops BinaryWriter/BinaryReader intermediary;
        //      strings use int32 byte-count prefix instead of 7-bit
        //      variable-length encoding.
        public const byte FormatVersion = 2;

        // magic0 (1) + magic1 (1) + formatVersion (1) + version (4) + flags (8) + includeTypeChecks (1)
        const int HeaderSize = 16;

        public const int Size = HeaderSize;

        public static void WriteHeader(
            Stream stream,
            int version,
            long flags,
            bool includeTypeChecks
        )
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            FormatHeader(header, version, flags, includeTypeChecks);
            stream.Write(header);
        }

        public static void WriteHeader(
            Span<byte> dest,
            int version,
            long flags,
            bool includeTypeChecks
        )
        {
            TrecsDebugAssert.That(dest.Length >= HeaderSize);
            FormatHeader(dest, version, flags, includeTypeChecks);
        }

        static void FormatHeader(Span<byte> header, int version, long flags, bool includeTypeChecks)
        {
            header[0] = MagicByte0;
            header[1] = MagicByte1;
            header[2] = FormatVersion;
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(3), version);
            BinaryPrimitives.WriteInt64LittleEndian(header.Slice(7), flags);
            header[15] = includeTypeChecks ? (byte)1 : (byte)0;
        }

        public static (int version, long flags, bool includeTypeChecks) ReadHeader(Stream stream)
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            int bytesRead = stream.Read(header);

            if (bytesRead < HeaderSize)
            {
                throw new SerializationException(
                    $"Truncated header — expected {HeaderSize} bytes but got {bytesRead}."
                );
            }

            return ParseHeader(header);
        }

        public static (int version, long flags, bool includeTypeChecks) ReadHeader(
            ReadOnlySpan<byte> data,
            ref int offset
        )
        {
            if (offset + HeaderSize > data.Length)
            {
                throw new SerializationException(
                    $"Truncated header — expected {HeaderSize} bytes at offset {offset} but data length is only {data.Length}."
                );
            }

            var result = ParseHeader(data.Slice(offset, HeaderSize));
            offset += HeaderSize;
            return result;
        }

        static (int version, long flags, bool includeTypeChecks) ParseHeader(
            ReadOnlySpan<byte> header
        )
        {
            // Magic + format-version checks throw SerializationException, not
            // TrecsDebugAssert, because they need to fire in release builds.
            var magic0 = header[0];
            var magic1 = header[1];
            if (magic0 != MagicByte0 || magic1 != MagicByte1)
            {
                throw new SerializationException(
                    $"Invalid serialization magic bytes — not a Trecs-serialized "
                        + $"payload (got {magic0:X2}{magic1:X2}, expected "
                        + $"{MagicByte0:X2}{MagicByte1:X2})."
                );
            }

            var formatVersion = header[2];
            if (formatVersion != FormatVersion)
            {
                throw new SerializationException(
                    $"Unsupported Trecs serialization format version {formatVersion} "
                        + $"(expected {FormatVersion}). Payload was written by a different "
                        + "version of Trecs."
                );
            }

            var version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(3));
            var flags = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(7));
            var includeTypeChecks = header[15] != 0;
            return (version, flags, includeTypeChecks);
        }
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// Header fields read from the start of a serialized payload. Obtain via
    /// <see cref="PayloadHeader.Peek(Stream)"/> or <see cref="PayloadHeader.Peek(string)"/>
    /// for pre-flight validation — e.g. asserting a recording was produced with
    /// the flags/version your code expects before starting a full deserialization.
    /// </summary>
    public readonly struct PayloadHeader
    {
        public int Version { get; }
        public long Flags { get; }
        public bool IncludeTypeChecks { get; }

        public PayloadHeader(int version, long flags, bool includeTypeChecks)
        {
            Version = version;
            Flags = flags;
            IncludeTypeChecks = includeTypeChecks;
        }

        public bool HasFlag(long flag) => (Flags & flag) != 0;

        /// <summary>
        /// Read just the header from <paramref name="stream"/> without consuming
        /// the rest of the payload. The stream's position is restored to where
        /// it was before the call — on both success and failure — so a full
        /// read can still proceed afterwards, and callers can distinguish
        /// "not a valid Trecs payload" from "stream already advanced" cleanly.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static PayloadHeader Peek(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var startPosition = stream.Position;
            try
            {
                var (version, flags, includeTypeChecks) = SerializationHeaderUtil.ReadHeader(
                    stream
                );
                return new PayloadHeader(version, flags, includeTypeChecks);
            }
            finally
            {
                stream.Position = startPosition;
            }
        }

        /// <summary>
        /// Read just the header from the file at <paramref name="filePath"/>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="filePath"/>.</exception>
        public static PayloadHeader Peek(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Payload file not found", filePath);

            using var fs = File.OpenRead(filePath);
            return Peek(fs);
        }
    }
}
