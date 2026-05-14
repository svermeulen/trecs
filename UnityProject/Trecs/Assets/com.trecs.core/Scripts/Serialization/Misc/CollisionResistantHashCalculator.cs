using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    /// <summary>
    /// Collision-resistant hash functions optimized for generating unique identifiers (GIDs).
    /// Slower than a trivial byte-sum but much faster than cryptographic hashes, with
    /// significantly better collision resistance than FNV-1a.
    ///
    /// All entry points here are zero-allocation: xxHash64/xxHash128 use unsafe pointer
    /// operations directly on the input span.
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
        const ulong PRIME64_1 = 11400714785074694791UL;
        const ulong PRIME64_2 = 14029467366897019727UL;
        const ulong PRIME64_3 = 1609587929392839161UL;
        const ulong PRIME64_4 = 9650029242287828579UL;
        const ulong PRIME64_5 = 2870177450012600261UL;

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
        static ulong Round(ulong acc, ulong input)
        {
            return RotateLeft(acc + (input * PRIME64_2), 31) * PRIME64_1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong MergeRound(ulong acc, ulong val)
        {
            return (acc ^ Round(0, val)) * PRIME64_1 + PRIME64_4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong RotateLeft(ulong value, int offset)
        {
            return (value << offset) | (value >> (64 - offset));
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
