using System;
using Trecs.Internal;

namespace Trecs
{
    public static class BlobIdGenerator
    {
        /// <summary>
        /// Build a <see cref="BlobId"/> as the xxHash64 of raw bytes. Use this for true
        /// content-addressable lookup over already-serialized data. For typed values, prefer
        /// <see cref="FromContent{T}(WorldAccessor, in T)"/>, which serializes and hashes for you.
        /// Empty input throws.
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

        /// <summary>
        /// Build a content-addressed <see cref="BlobId"/> from a typed <paramref name="value"/> by
        /// serializing it (with <paramref name="world"/>'s serializer registry) and hashing the bytes.
        /// Equal values produce the same id, so this is the way to derive a stable, dedup-friendly id
        /// for content you compute on the fly: hash the value, check residency, and on a miss build it
        /// and store under the id via <c>Alloc(world, id, ...)</c>. <typeparamref name="T"/> must be
        /// registered for serialization; main-thread only.
        /// </summary>
        public static BlobId FromContent<T>(WorldAccessor world, in T value)
        {
            return world.BlobFactory.DeriveContentId(in value);
        }

        /// <summary>
        /// Build a content-addressed <see cref="BlobId"/> from a typed <paramref name="value"/> using an
        /// explicit <paramref name="hashGenerator"/> — for callers that hash against a serializer
        /// registry but have no <see cref="WorldAccessor"/> (e.g. disk memoization keyed off a
        /// descriptor). Equal values produce the same id. <typeparamref name="T"/> must be registered
        /// for serialization; main-thread only.
        /// </summary>
        public static BlobId FromContent<T>(UniqueHashGenerator hashGenerator, in T value)
        {
            return new BlobId(hashGenerator.Generate(in value));
        }
    }
}
