using System;
using Trecs.Collections;

namespace Trecs
{
    [TypeId(378502946)]
    public struct BlobMetadata
    {
        public Type Type;
        public long LastAccessTime;
        public long NumBytes;
        public bool IsNative;
    }

    [TypeId(767600239)]
    public class BlobManifest
    {
        public readonly DenseDictionary<BlobId, BlobMetadata> Values = new();

        public static long GetTimeForAccessTime()
        {
            return DateTime.Now.Ticks;
        }
    }
}
