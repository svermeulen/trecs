using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Trecs.Internal
{
    /// <summary>
    /// Collision-resistant hash functions optimized for generating unique identifiers (GIDs).
    /// Slower than a trivial byte-sum but much faster than cryptographic hashes, with
    /// significantly better collision resistance than FNV-1a.
    ///
    /// All entry points here are zero-allocation: xxHash64/xxHash128 use unsafe pointer
    /// operations directly on the input span, and the string overload encodes into a
    /// reused scratch buffer (main-thread only; allocates only when the buffer grows).
    /// </summary>
    public static class CollisionResistantHashCalculator
    {
        /// <summary>
        /// Computes a 128-bit xxHash and returns it as two longs for excellent collision resistance.
        /// xxHash has much better avalanche properties than FNV and is still very fast.
        /// Collision probability: ~1 in 2^128 (practically impossible).
        /// </summary>
        public static (long high, long low) ComputeXxHash128(byte[] buffer, int length)
        {
            return XxHash128.Hash(buffer, length);
        }

        /// <summary>
        /// Zero-allocation overload: computes a 128-bit xxHash directly from an unsafe pointer.
        /// Use this when you already have unsafe contexts for maximum performance.
        /// </summary>
        public static unsafe (long high, long low) ComputeXxHash128(byte* buffer, int length)
        {
            // Use two xxHash64 with different seeds for simplicity and speed
            ulong hash1 = XxHash64.HashInternal(buffer, length, 0);
            ulong hash2 = XxHash64.HashInternal(buffer, length, 0xAAAAAAAAAAAAAAAAUL);

            return ((long)hash1, (long)hash2);
        }

        /// <summary>
        /// Computes a 64-bit xxHash for good collision resistance with smaller output.
        /// Much faster than cryptographic hashes but much more collision-resistant than FNV.
        /// Collision probability: ~1 in 2^64.
        /// </summary>
        public static ulong ComputeXxHash64(byte[] buffer, int length)
        {
            return XxHash64.Hash(buffer, length);
        }

        // Reused UTF-8 scratch buffer for the string overload — grown as
        // needed, retained for the app lifetime. Main-thread only (asserted
        // below), matching the pattern used for other static mutable caches
        // (TypeIdSetRegistry, PrettyTypeNameCache).
        static byte[] _utf8Scratch = new byte[256];

        /// <summary>
        /// 64-bit xxHash of the UTF-8 bytes of <paramref name="text"/> —
        /// name-derived ids that are stable across sessions and platforms.
        /// Zero-allocation in steady state (encodes into a reused scratch
        /// buffer). Main-thread only.
        /// </summary>
        public static ulong ComputeXxHash64(string text)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            var utf8 = Encoding.UTF8;
            var byteCount = utf8.GetByteCount(text);
            if (_utf8Scratch.Length < byteCount)
            {
                _utf8Scratch = new byte[Math.Max(byteCount, _utf8Scratch.Length * 2)];
            }
            var written = utf8.GetBytes(text, 0, text.Length, _utf8Scratch, 0);
            return ComputeXxHash64(_utf8Scratch, written);
        }

        public static ulong ComputeXxHash64(ReadOnlyMemory<byte> data)
        {
            return ComputeXxHash64(data.Span);
        }

        public static unsafe ulong ComputeXxHash64(ReadOnlySpan<byte> data)
        {
            fixed (byte* ptr = data)
            {
                return XxHash64.HashInternal(ptr, data.Length, 0);
            }
        }

        /// <summary>
        /// Zero-allocation overload: computes a 64-bit xxHash directly from an unsafe pointer.
        /// Use this when you already have unsafe contexts for maximum performance.
        /// </summary>
        public static unsafe ulong ComputeXxHash64(byte* buffer, int length)
        {
            return XxHash64.HashInternal(buffer, length, 0);
        }
    }

    /// <summary>
    /// Fast implementation of xxHash64.
    /// Known for excellent speed and distribution properties.
    /// </summary>
    static class XxHash64
    {
        internal const ulong PRIME64_1 = 11400714785074694791UL;
        internal const ulong PRIME64_2 = 14029467366897019727UL;
        internal const ulong PRIME64_3 = 1609587929392839161UL;
        internal const ulong PRIME64_4 = 9650029242287828579UL;
        internal const ulong PRIME64_5 = 2870177450012600261UL;

        public static unsafe ulong Hash(byte[] buffer, int length, ulong seed = 0)
        {
            fixed (byte* ptr = buffer)
            {
                return HashInternal(ptr, length, seed);
            }
        }

        public static unsafe ulong HashInternal(byte* input, int length, ulong seed)
        {
            ulong hash;

            if (length >= 32)
            {
                ulong v1 = seed + PRIME64_1 + PRIME64_2;
                ulong v2 = seed + PRIME64_2;
                ulong v3 = seed + 0;
                ulong v4 = seed - PRIME64_1;

                ulong* p = (ulong*)input;
                ulong* limit = p + (length / 32) * 4;

                do
                {
                    v1 = Round(v1, *p++);
                    v2 = Round(v2, *p++);
                    v3 = Round(v3, *p++);
                    v4 = Round(v4, *p++);
                } while (p < limit);

                hash =
                    RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);

                hash = MergeRound(hash, v1);
                hash = MergeRound(hash, v2);
                hash = MergeRound(hash, v3);
                hash = MergeRound(hash, v4);

                input = (byte*)p;
                length &= 31;
            }
            else
            {
                hash = seed + PRIME64_5;
            }

            hash += (ulong)length;

            // Process remaining bytes
            while (length >= 8)
            {
                hash ^= Round(0, *(ulong*)input);
                hash = RotateLeft(hash, 27) * PRIME64_1 + PRIME64_4;
                input += 8;
                length -= 8;
            }

            while (length >= 4)
            {
                hash ^= (*(uint*)input) * PRIME64_1;
                hash = RotateLeft(hash, 23) * PRIME64_2 + PRIME64_3;
                input += 4;
                length -= 4;
            }

            while (length > 0)
            {
                hash ^= *input * PRIME64_5;
                hash = RotateLeft(hash, 11) * PRIME64_1;
                input++;
                length--;
            }

            // Avalanche
            hash ^= hash >> 33;
            hash *= PRIME64_2;
            hash ^= hash >> 29;
            hash *= PRIME64_3;
            hash ^= hash >> 32;

            return hash;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong Round(ulong acc, ulong input)
        {
            return RotateLeft(acc + (input * PRIME64_2), 31) * PRIME64_1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong MergeRound(ulong acc, ulong val)
        {
            return (acc ^ Round(0, val)) * PRIME64_1 + PRIME64_4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong RotateLeft(ulong value, int offset)
        {
            return (value << offset) | (value >> (64 - offset));
        }
    }

    /// <summary>
    /// Incremental (streaming) xxHash64. <see cref="Digest"/> returns a value
    /// byte-identical to
    /// <see cref="CollisionResistantHashCalculator.ComputeXxHash64(ReadOnlySpan{byte})"/>
    /// over the concatenation of every <see cref="Update"/> input, regardless of how the
    /// bytes are split across calls. This lets callers hash several disjoint spans — e.g. a
    /// serialized payload's synthesized header + bit-field + data sections — without first
    /// copying them into one contiguous buffer.
    /// </summary>
    internal unsafe struct XxHash64Builder
    {
        ulong _v1,
            _v2,
            _v3,
            _v4;
        ulong _seed;
        ulong _totalLen;
        fixed byte _buffer[32];
        int _bufferSize;

        public static XxHash64Builder Create(ulong seed = 0)
        {
            XxHash64Builder b = default;
            b._seed = seed;
            b._v1 = seed + XxHash64.PRIME64_1 + XxHash64.PRIME64_2;
            b._v2 = seed + XxHash64.PRIME64_2;
            b._v3 = seed + 0;
            b._v4 = seed - XxHash64.PRIME64_1;
            return b;
        }

        public void Update(ReadOnlySpan<byte> input)
        {
            if (input.IsEmpty)
                return;

            _totalLen += (ulong)input.Length;

            fixed (byte* pInput = input)
            fixed (byte* pBuf = _buffer)
            {
                byte* p = pInput;
                byte* pEnd = pInput + input.Length;

                // Not enough buffered+new bytes to complete a 32-byte block: stash and return.
                if (_bufferSize + input.Length < 32)
                {
                    Buffer.MemoryCopy(p, pBuf + _bufferSize, 32 - _bufferSize, input.Length);
                    _bufferSize += input.Length;
                    return;
                }

                // Drain a partially-filled stash by topping it up to a full 32-byte block.
                if (_bufferSize > 0)
                {
                    int fill = 32 - _bufferSize;
                    Buffer.MemoryCopy(p, pBuf + _bufferSize, fill, fill);
                    ulong* b = (ulong*)pBuf;
                    _v1 = XxHash64.Round(_v1, b[0]);
                    _v2 = XxHash64.Round(_v2, b[1]);
                    _v3 = XxHash64.Round(_v3, b[2]);
                    _v4 = XxHash64.Round(_v4, b[3]);
                    p += fill;
                    _bufferSize = 0;
                }

                // Process full 32-byte blocks straight from the input.
                if (p + 32 <= pEnd)
                {
                    ulong v1 = _v1,
                        v2 = _v2,
                        v3 = _v3,
                        v4 = _v4;
                    do
                    {
                        ulong* pp = (ulong*)p;
                        v1 = XxHash64.Round(v1, pp[0]);
                        v2 = XxHash64.Round(v2, pp[1]);
                        v3 = XxHash64.Round(v3, pp[2]);
                        v4 = XxHash64.Round(v4, pp[3]);
                        p += 32;
                    } while (p + 32 <= pEnd);
                    _v1 = v1;
                    _v2 = v2;
                    _v3 = v3;
                    _v4 = v4;
                }

                // Stash the trailing partial block for the next Update / Digest.
                int remaining = (int)(pEnd - p);
                if (remaining > 0)
                {
                    Buffer.MemoryCopy(p, pBuf, 32, remaining);
                    _bufferSize = remaining;
                }
            }
        }

        public ulong Digest()
        {
            ulong hash;
            if (_totalLen >= 32)
            {
                hash =
                    XxHash64.RotateLeft(_v1, 1)
                    + XxHash64.RotateLeft(_v2, 7)
                    + XxHash64.RotateLeft(_v3, 12)
                    + XxHash64.RotateLeft(_v4, 18);
                hash = XxHash64.MergeRound(hash, _v1);
                hash = XxHash64.MergeRound(hash, _v2);
                hash = XxHash64.MergeRound(hash, _v3);
                hash = XxHash64.MergeRound(hash, _v4);
            }
            else
            {
                hash = _seed + XxHash64.PRIME64_5;
            }

            // NB: this matches XxHash64.HashInternal, which adds the *remainder* length
            // (length & 31) after block processing — not the canonical total length. The
            // leftover bytes in _buffer ARE that remainder (and equal the total when < 32),
            // so adding _bufferSize reproduces the same value.
            hash += (ulong)_bufferSize;

            fixed (byte* pBuf = _buffer)
            {
                byte* input = pBuf;
                int length = _bufferSize;

                while (length >= 8)
                {
                    hash ^= XxHash64.Round(0, *(ulong*)input);
                    hash = XxHash64.RotateLeft(hash, 27) * XxHash64.PRIME64_1 + XxHash64.PRIME64_4;
                    input += 8;
                    length -= 8;
                }
                while (length >= 4)
                {
                    hash ^= (*(uint*)input) * XxHash64.PRIME64_1;
                    hash = XxHash64.RotateLeft(hash, 23) * XxHash64.PRIME64_2 + XxHash64.PRIME64_3;
                    input += 4;
                    length -= 4;
                }
                while (length > 0)
                {
                    hash ^= *input * XxHash64.PRIME64_5;
                    hash = XxHash64.RotateLeft(hash, 11) * XxHash64.PRIME64_1;
                    input++;
                    length--;
                }
            }

            hash ^= hash >> 33;
            hash *= XxHash64.PRIME64_2;
            hash ^= hash >> 29;
            hash *= XxHash64.PRIME64_3;
            hash ^= hash >> 32;
            return hash;
        }
    }

    /// <summary>
    /// Fast implementation of xxHash128 for maximum collision resistance.
    /// </summary>
    static class XxHash128
    {
        public static unsafe (long high, long low) Hash(byte[] buffer, int length, ulong seed = 0)
        {
            fixed (byte* ptr = buffer)
            {
                // Two xxHash64 with different seeds
                ulong hash1 = XxHash64.HashInternal(ptr, length, seed);
                ulong hash2 = XxHash64.HashInternal(ptr, length, seed ^ 0xAAAAAAAAAAAAAAAAUL);

                return ((long)hash1, (long)hash2);
            }
        }
    }
}
