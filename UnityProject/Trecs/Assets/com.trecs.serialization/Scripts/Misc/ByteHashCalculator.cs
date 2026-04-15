using System.Runtime.CompilerServices;

namespace Trecs.Serialization
{
    // This class provides a fast hash calculation for byte arrays using the FNV-1a hash algorithm.
    // Note that this is a good hash for dictionaries etc. but not secure, and also, should not
    // be used to uniquely identify data, as there is a high risk of collisions, even though the
    // algorithm does have good distribution properties
    public static class ByteHashCalculator
    {
        const uint FNV_PRIME = 16777619;
        const uint FNV_OFFSET_BASIS = 2166136261;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint FnvHash(uint hash, uint value)
        {
            return (hash ^ value) * FNV_PRIME;
        }

        public static unsafe uint Run(byte[] buffer, int length)
        {
            uint hash = FNV_OFFSET_BASIS;

            if (length == 0)
            {
                return hash;
            }

            fixed (byte* ptr = buffer)
            {
                uint* uintPtr = (uint*)ptr;
                int uintCount = length / sizeof(uint);

                // Process 4 bytes at a time
                for (int i = 0; i < uintCount; i++)
                {
                    hash = FnvHash(hash, *uintPtr);
                    uintPtr++;
                }

                // Process remaining bytes
                byte* remaining = (byte*)uintPtr;
                for (int i = 0; i < length % sizeof(uint); i++)
                {
                    hash = FnvHash(hash, *remaining);
                    remaining++;
                }
            }

            return hash;
        }
    }
}
