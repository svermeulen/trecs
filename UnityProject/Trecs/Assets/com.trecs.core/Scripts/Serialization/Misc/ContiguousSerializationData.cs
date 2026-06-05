using System;

namespace Trecs.Internal
{
    /// <summary>
    /// Zero-copy <see cref="IReadOnlySerializationData"/> read view over a single, self-contained
    /// contiguous payload (header + section length prefix + bit-fields + data) — the
    /// on-disk/over-the-wire form produced by <see cref="SerializationData.WriteContiguousTo(System.IO.Stream)"/>
    /// (e.g. a loaded <c>.snap</c> file). <see cref="Wrap"/> parses the header and slices the
    /// bit-field and data sections straight out of the supplied memory without copying, so the
    /// reader can consume a loaded payload through the same <see cref="Trecs.BinarySerializationReader.Start(IReadOnlySerializationData)"/>
    /// entry point as a retained in-memory <see cref="SerializationData"/>.
    ///
    /// <para>Reusable: a holder can keep one instance and re-<see cref="Wrap"/> a new payload per
    /// load, so there is no per-load allocation. The wrapped memory is borrowed, not owned — the
    /// caller must keep it alive for as long as this view (and any reader started from it) is in use.</para>
    ///
    /// <para>The data section's length is read from the explicit prefix, so any self-describing
    /// payload works — including one embedded with bytes after it (the view slices exactly the
    /// payload region and ignores the rest).</para>
    /// </summary>
    public sealed class ContiguousSerializationData : IReadOnlySerializationData
    {
        ReadOnlyMemory<byte> _bitFields; // slice of the wrapped payload
        ReadOnlyMemory<byte> _data; // slice of the wrapped payload

        int _version;
        long _flags;
        bool _includeTypeChecks;
        int _bitFieldBitCount;

        public ContiguousSerializationData() { }

        public ContiguousSerializationData(ReadOnlyMemory<byte> payload) => Wrap(payload);

        /// <summary>
        /// Point this view at <paramref name="payload"/>, parsing its header and slicing out the
        /// bit-field and data sections (no copy). Validates structure in release builds — magic
        /// bytes / format version (via the header parse) and the explicit section-length bounds —
        /// throwing <see cref="SerializationException"/> on a truncated or corrupt payload.
        /// </summary>
        public void Wrap(ReadOnlyMemory<byte> payload)
        {
            var layout = SerializationEnvelope.Parse(payload.Span);

            _version = layout.Version;
            _flags = layout.Flags;
            _includeTypeChecks = layout.IncludeTypeChecks;
            _bitFieldBitCount = layout.BitFieldBitCount;
            _bitFields = payload.Slice(layout.BitFieldStart, layout.BitFieldByteCount);
            _data = payload.Slice(layout.DataStart, layout.DataByteCount);
        }

        public int Version => _version;
        public long Flags => _flags;
        public bool IncludeTypeChecks => _includeTypeChecks;

        public bool HasFlag(long flag) => (_flags & flag) != 0;

        public ReadOnlyMemory<byte> BitFieldBytes => _bitFields;
        public int BitFieldBitCount => _bitFieldBitCount;
        public ReadOnlyMemory<byte> Data => _data;
    }
}
