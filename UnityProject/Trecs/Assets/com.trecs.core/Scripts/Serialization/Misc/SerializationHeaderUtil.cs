using System;
using System.ComponentModel;
using System.IO;
using System.Text;

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
        public const byte FormatVersion = 1;

        public static void WriteHeader(
            BinaryWriter writer,
            int version,
            long flags,
            bool includeTypeChecks
        )
        {
            writer.Write(MagicByte0);
            writer.Write(MagicByte1);
            writer.Write(FormatVersion);
            writer.Write(version);
            writer.Write(flags);
            writer.Write(includeTypeChecks);
        }

        public static (int version, long flags, bool includeTypeChecks) ReadHeader(
            BinaryReader reader
        )
        {
            // Magic + format-version checks throw SerializationException, not
            // TrecsDebugAssert, because they need to fire in release builds.
            // Letting a non-Trecs payload silently pass through here would
            // cause downstream reads to interpret arbitrary bytes as field
            // values and produce a much more confusing failure mode.
            var magic0 = reader.ReadByte();
            var magic1 = reader.ReadByte();
            if (magic0 != MagicByte0 || magic1 != MagicByte1)
            {
                throw new SerializationException(
                    $"Invalid serialization magic bytes — not a Trecs-serialized "
                        + $"payload (got {magic0:X2}{magic1:X2}, expected "
                        + $"{MagicByte0:X2}{MagicByte1:X2})."
                );
            }

            var formatVersion = reader.ReadByte();
            if (formatVersion != FormatVersion)
            {
                throw new SerializationException(
                    $"Unsupported Trecs serialization format version {formatVersion} "
                        + $"(expected {FormatVersion}). Payload was written by a different "
                        + "version of Trecs."
                );
            }

            var version = reader.ReadInt32();
            var flags = reader.ReadInt64();
            var includeTypeChecks = reader.ReadBoolean();
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
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                var (version, flags, includeTypeChecks) = SerializationHeaderUtil.ReadHeader(
                    reader
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
