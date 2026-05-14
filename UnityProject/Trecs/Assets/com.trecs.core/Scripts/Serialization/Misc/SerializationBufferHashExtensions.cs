using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Collision-resistant hash helper on top of <see cref="SerializationBuffer"/>.
    ///
    /// Zero-allocation: uses <see cref="System.IO.MemoryStream.GetBuffer"/> (returns the
    /// internal buffer without copying) and unsafe pointer operations for the xxHash
    /// calculation.
    /// </summary>
    public static class SerializationBufferHashExtensions
    {
        /// <summary>
        /// Gets a collision-resistant 64-bit hash suitable for use as a GID.
        /// </summary>
        public static long GetMemoryStreamCollisionResistantHash(
            this SerializationBuffer cacheHelper
        )
        {
            TrecsAssert.That(cacheHelper.MemoryPosition == 0);

            int length = (int)cacheHelper.MemoryStream.Length;
            TrecsAssert.That(length > 0);

            byte[] buffer = cacheHelper.MemoryStream.GetBuffer();
            return unchecked(
                (long)CollisionResistantHashCalculator.ComputeXxHash64(buffer, length)
            );
        }
    }
}
