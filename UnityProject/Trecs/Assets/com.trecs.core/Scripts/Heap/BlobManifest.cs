using System;
using Trecs.Collections;

namespace Trecs
{
    /// <summary>
    /// Metadata for a single blob entry in a <see cref="BlobManifest"/>.
    /// </summary>
    [TypeId(378502946)]
    public struct BlobMetadata
    {
        public Type Type;
        public long LastAccessTime;
        public long NumBytes;
        public bool IsNative;
    }

    /// <summary>
    /// Index of all known blobs in a <see cref="IBlobStore"/>, mapping
    /// <see cref="BlobId"/> to <see cref="BlobMetadata"/>.
    /// </summary>
    [TypeId(767600239)]
    public sealed class BlobManifest
    {
        public readonly DenseDictionary<BlobId, BlobMetadata> Values = new();

        public static long GetTimeForAccessTime()
        {
            return DateTime.Now.Ticks;
        }
    }
}
