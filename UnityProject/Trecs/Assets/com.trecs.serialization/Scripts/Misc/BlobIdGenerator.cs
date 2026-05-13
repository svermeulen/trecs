using System;
using Trecs.Serialization.Internal;

namespace Trecs.Serialization
{
    public static class BlobIdGenerator
    {
        /// <summary>
        /// Build a <see cref="BlobId"/> directly from a long-valued domain key
        /// (level id, prefab id, etc.). Caller is responsible for namespacing —
        /// two unrelated subsystems that pick the same long will collide.
        /// Zero is reserved for <see cref="Null"/>; passing it throws.
        /// </summary>
        public static BlobId FromKey(long key)
        {
            if (key == 0)
            {
                throw new ArgumentException(
                    "BlobIdGenerator.FromKey(0) is reserved for BlobId.Null",
                    nameof(key)
                );
            }
            return new BlobId(key);
        }

        /// <summary>
        /// Build a <see cref="BlobId"/> as the xxHash64 of raw bytes. Use this for
        /// true content-addressable lookup over already-serialized data. For typed
        /// values, hash the value via <c>UniqueHashGenerator</c> in
        /// <c>Trecs.Serialization</c> and pass the result through <see cref="FromKey"/>
        /// or this method. Empty input throws.
        /// </summary>
        public static unsafe BlobId FromBytes(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
            {
                throw new ArgumentException(
                    "BlobId.FromBytes requires at least one byte",
                    nameof(bytes)
                );
            }
            fixed (byte* p = bytes)
            {
                long h = unchecked(
                    (long)CollisionResistantHashCalculator.ComputeXxHash64(p, bytes.Length)
                );
                if (h == 0)
                {
                    h = 1; // Avoid colliding with BlobId.Null.
                }
                return new BlobId(h);
            }
        }
    }
}
