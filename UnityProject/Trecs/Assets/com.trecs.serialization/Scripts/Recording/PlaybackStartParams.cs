using Trecs.Collections;

namespace Trecs.Serialization
{
    public class PlaybackStartParams
    {
        public SerializationBuffer SerializerHelper;
        public ReadOnlyDenseHashSet<int> SerializationFlags;
        public bool InputsOnly;
        public int Version;
    }
}
