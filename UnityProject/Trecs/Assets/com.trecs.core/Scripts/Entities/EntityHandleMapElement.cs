using System.ComponentModel;

namespace Trecs.Internal
{
    [TypeId(592038417)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct EntityHandleMapElement
    {
        internal EntityIndex entityIndex;
        internal int version;

        internal EntityHandleMapElement(EntityIndex entityIndex)
        {
            this.entityIndex = entityIndex;
            version = 0;
        }

        internal EntityHandleMapElement(EntityIndex entityIndex, int version)
        {
            this.entityIndex = entityIndex;
            this.version = version;
        }
    }
}
