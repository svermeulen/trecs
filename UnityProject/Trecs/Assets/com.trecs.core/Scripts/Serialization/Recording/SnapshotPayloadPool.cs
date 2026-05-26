using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Trecs.Internal
{
    /// <summary>
    /// Recycling pool for snapshot payload byte arrays. Multi-MB snapshot
    /// allocations live on the LOH and would trigger Gen 2 GC on every
    /// capture; this pool recycles them. Bounded by
    /// <see cref="MaxBuffers"/> to cap resident LOH memory.
    /// </summary>
    public sealed class SnapshotPayloadPool : IDisposable
    {
        public const int DefaultMaxBuffers = 64;

        readonly Stack<byte[]> _pool = new();
#if DEBUG
        readonly HashSet<byte[]> _poolSet = new();
#endif

        public int MaxBuffers { get; }

        public SnapshotPayloadPool(int maxBuffers = DefaultMaxBuffers)
        {
            if (maxBuffers < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxBuffers),
                    maxBuffers,
                    "maxBuffers must be >= 0"
                );
            MaxBuffers = maxBuffers;
        }

        /// <summary>
        /// Rent a byte[] from the pool (or allocate a fresh one) with at least
        /// <paramref name="minLength"/> bytes. The caller writes directly into the
        /// buffer, then wraps it as <see cref="ReadOnlyMemory{T}"/>.
        /// </summary>
        public byte[] Rent(int minLength)
        {
            while (_pool.Count > 0)
            {
                var candidate = _pool.Pop();
#if DEBUG
                if (candidate != null)
                {
                    _poolSet.Remove(candidate);
                }
#endif
                if (candidate != null && candidate.Length >= minLength)
                {
                    return candidate;
                }
            }
            return new byte[minLength];
        }

        /// <summary>
        /// Copy <paramref name="length"/> bytes from <paramref name="source"/>
        /// into a pooled (or freshly allocated) buffer and return an exact-length
        /// <see cref="ReadOnlyMemory{T}"/> view over it. The backing array may be
        /// larger than <paramref name="length"/>.
        /// </summary>
        public ReadOnlyMemory<byte> RentAndCopy(byte[] source, int length)
        {
            byte[] buffer = null;
            while (_pool.Count > 0)
            {
                var candidate = _pool.Pop();
#if DEBUG
                if (candidate != null)
                {
                    _poolSet.Remove(candidate);
                }
#endif
                if (candidate != null && candidate.Length >= length)
                {
                    buffer = candidate;
                    break;
                }
            }
            if (buffer == null)
            {
                buffer = new byte[length];
            }
            Buffer.BlockCopy(source, 0, buffer, 0, length);
            return new ReadOnlyMemory<byte>(buffer, 0, length);
        }

        /// <summary>
        /// Return a payload previously obtained via <see cref="RentAndCopy"/>
        /// to the pool. No-op for empty or non-array-backed payloads. Past the
        /// cap, the buffer is dropped for the GC to reclaim.
        ///
        /// <para>Calling this twice with the same payload is a bug — the buffer
        /// would enter the pool twice. DEBUG builds assert on double-return.</para>
        /// </summary>
        public void Return(ReadOnlyMemory<byte> payload)
        {
            if (payload.IsEmpty)
            {
                return;
            }
            if (_pool.Count >= MaxBuffers)
            {
                return;
            }
            if (
                MemoryMarshal.TryGetArray(payload, out var seg)
                && seg.Array != null
                && seg.Array.Length > 0
            )
            {
#if DEBUG
                TrecsDebugAssert.That(
                    _poolSet.Add(seg.Array),
                    "SnapshotPayloadPool buffer returned twice — caller tracking is broken"
                );
#endif
                _pool.Push(seg.Array);
            }
        }

        public void Dispose()
        {
            _pool.Clear();
#if DEBUG
            _poolSet.Clear();
#endif
        }
    }
}
