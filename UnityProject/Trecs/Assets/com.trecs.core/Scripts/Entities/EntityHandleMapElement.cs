using System.ComponentModel;

namespace Trecs.Internal
{
    [TypeId(592038417)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal struct EntityHandleMapElement
    {
        internal int Index;
        internal GroupIndex GroupIndex; // 2 bytes (ushort)
        internal ushort Version; // 2 bytes

        /// <summary>
        /// Convenience accessor: reconstructs the EntityIndex from the packed fields.
        /// </summary>
        internal EntityIndex EntityIndex => new EntityIndex(Index, GroupIndex);

        internal EntityHandleMapElement(EntityIndex entityIndex)
            : this(entityIndex.Index, entityIndex.GroupIndex, 0) { }

        internal EntityHandleMapElement(EntityIndex entityIndex, int version)
            : this(entityIndex.Index, entityIndex.GroupIndex, checked((ushort)version)) { }

        internal EntityHandleMapElement(int index, GroupIndex groupIndex, ushort version)
        {
            Index = index;
            GroupIndex = groupIndex;
            Version = version;
        }

        internal void BumpVersion()
        {
            Version = (ushort)(Version + 1);
        }
    }
}
