using System.Runtime.CompilerServices;

namespace Trecs.Serialization.Internal
{
    // Fast FNV-1a byte-array hash. Intended use: non-cryptographic fingerprinting
    // for sanity checks (e.g. recording/playback desync detection), dictionary
    // keys, and similar best-effort checks. It is not collision-resistant in the
    // cryptographic sense and must not be used for security or for content-
    // addressed storage where uniqueness is load-bearing.
    //
    // For desync detection specifically, occasional collisions are acceptable:
    // a missed desync on one checksum frame will be caught on the next
    // checksum frame with very high probability, since real desyncs diverge
    // further each frame. The 32-bit output is a deliberate trade-off for
    // speed and storage density in the recording bundle's per-frame
    // checksums dictionary.
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
