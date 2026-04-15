using Trecs.Collections;

namespace Trecs.Serialization
{
    public class SerializationConstants
    {
        public static readonly ReadOnlyDenseHashSet<int> DefaultFlags = new DenseHashSet<int>();

        // Sentinel value written at end of serialization to detect stream corruption
        public static readonly byte SentinelValue = 0x5E;
    }
}
