using System;
using System.Buffers;
using System.IO;

namespace Trecs.Internal
{
    /// <summary>
    /// Poolable in-memory representation of a serialized payload: a packed bit-field section
    /// and a data section (each an <see cref="ArrayBufferWriter{T}"/>), plus the universal
    /// header fields. A <see cref="Trecs.BinarySerializationWriter"/> can write straight into
    /// these buffers and a <see cref="Trecs.BinarySerializationReader"/> can read straight out
    /// of them, so a retained snapshot is the writer's own output rather than a copy of it.
    ///
    /// <para>The contiguous wire form (header + section length prefix + bit-fields + data) is
    /// synthesized on demand by <see cref="CopyContiguousTo"/> /
    /// <see cref="WriteContiguousTo(System.Buffers.IBufferWriter{byte})"/> /
    /// <see cref="WriteContiguousTo(System.IO.Stream)"/>.</para>
    ///
    /// Reused via <see cref="SerializationDataPool"/>; <see cref="Clear"/> resets contents
    /// while keeping buffer capacity. Main-thread only.
    /// </summary>
    public sealed class SerializationData : IReadOnlySerializationData
    {
        // Sized so a fresh instance holds a typical small payload without a growth ramp.
        // Bit-fields are tiny by comparison.
        const int InitialDataCapacity = 4 * 1024;
        const int InitialBitFieldCapacity = 64;

        readonly ArrayBufferWriter<byte> _bitFields = new(InitialBitFieldCapacity);
        readonly ArrayBufferWriter<byte> _data = new(InitialDataCapacity);

        public int Version { get; set; }
        public long Flags { get; set; }
        public bool IncludeTypeChecks { get; set; }

        /// <summary>Number of valid bits packed into <see cref="BitFieldsWriter"/>; stamped at finalize.</summary>
        public int BitFieldBitCount { get; set; }

        // Mutable write-side buffers the writer packs into. Internal: callers go through
        // BinarySerializationWriter, not these directly.
        internal ArrayBufferWriter<byte> BitFieldsWriter => _bitFields;
        internal ArrayBufferWriter<byte> DataWriter => _data;

        public ReadOnlyMemory<byte> BitFieldBytes => _bitFields.WrittenMemory;
        public ReadOnlyMemory<byte> Data => _data.WrittenMemory;

        public bool HasFlag(long flag) => (Flags & flag) != 0;

        public int ContiguousSize =>
            SerializationEnvelope.ContiguousSize(_bitFields.WrittenCount, _data.WrittenCount);

        /// <summary>
        /// Reset contents to empty while keeping the underlying buffer capacity, so the next
        /// use re-fills the same arrays instead of allocating.
        /// </summary>
        public void Clear()
        {
            _bitFields.Clear();
            _data.Clear();
            Version = 0;
            Flags = 0;
            IncludeTypeChecks = false;
            BitFieldBitCount = 0;
        }

        public void CopyContiguousTo(Span<byte> dest)
        {
            TrecsDebugAssert.That(
                dest.Length >= ContiguousSize,
                "Destination span ({0}) smaller than contiguous size ({1})",
                dest.Length,
                ContiguousSize
            );
            SerializationEnvelope.Write(
                dest,
                Version,
                Flags,
                IncludeTypeChecks,
                BitFieldBitCount,
                _bitFields.WrittenSpan,
                _data.WrittenSpan
            );
        }

        public void WriteContiguousTo(IBufferWriter<byte> writer)
        {
            int size = ContiguousSize;
            var span = writer.GetSpan(size);
            CopyContiguousTo(span);
            writer.Advance(size);
        }

        public void WriteContiguousTo(Stream stream) =>
            SerializationEnvelope.Write(
                stream,
                Version,
                Flags,
                IncludeTypeChecks,
                BitFieldBitCount,
                _bitFields.WrittenSpan,
                _data.WrittenSpan
            );

        public ulong ComputeContiguousChecksum() =>
            SerializationEnvelope.Checksum(
                Version,
                Flags,
                IncludeTypeChecks,
                BitFieldBitCount,
                _bitFields.WrittenSpan,
                _data.WrittenSpan
            );
    }
}
