using System;

namespace Trecs.Internal
{
    /// <summary>
    /// Read-only view over a serialized payload held as two in-memory sections — a packed
    /// bit-field section and a data section — plus the universal header fields. This is exactly
    /// what <see cref="Trecs.BinarySerializationReader"/> consumes: a retained in-memory
    /// <see cref="SerializationData"/>, or a <see cref="ContiguousSerializationData"/> view over a
    /// loaded blob.
    ///
    /// <para>The contiguous wire form (header + section length prefix + the two sections) is a
    /// producer concern that lives on <see cref="SerializationData"/>, not on this read surface —
    /// keeping the prefix out of the section buffers is what lets a writer pack bits straight into a
    /// pooled buffer without reserving/backfilling it.</para>
    /// </summary>
    public interface IReadOnlySerializationData
    {
        int Version { get; }
        long Flags { get; }
        bool IncludeTypeChecks { get; }
        bool HasFlag(long flag);

        /// <summary>Packed bit-field bytes only (no length prefix).</summary>
        ReadOnlyMemory<byte> BitFieldBytes { get; }

        /// <summary>Number of valid bits packed into <see cref="BitFieldBytes"/>.</summary>
        int BitFieldBitCount { get; }

        /// <summary>The data section bytes.</summary>
        ReadOnlyMemory<byte> Data { get; }
    }
}
