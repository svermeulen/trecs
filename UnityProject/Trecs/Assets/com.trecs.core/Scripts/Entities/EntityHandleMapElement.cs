using System.ComponentModel;

namespace Trecs.Internal
{
    [TypeId(592038417)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct EntityHandleMapElement
    {
        internal EntityIndex EntityIndex;
        internal int Version;

        internal EntityHandleMapElement(EntityIndex entityIndex)
        {
            EntityIndex = entityIndex;
            Version = 0;
        }

        internal EntityHandleMapElement(EntityIndex entityIndex, int version)
        {
            EntityIndex = entityIndex;
            Version = version;
        }
    }
}
