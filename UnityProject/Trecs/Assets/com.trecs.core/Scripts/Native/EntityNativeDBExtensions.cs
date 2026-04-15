using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class EntityNativeDBExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NativeBuffer<T> GetArrayAndEntityIndex<T>(
            this EntityIndexMapper<T> mapper,
            int index,
            out int outIndex
        )
            where T : unmanaged, IEntityComponent
        {
            outIndex = index;
            if (index < mapper._map.Count)
            {
                return mapper._map.GetValues(out _);
            }

            throw new TrecsException("Entity not found");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryGetArrayAndEntityIndex<T>(
            this EntityIndexMapper<T> mapper,
            int index,
            out int outIndex,
            out NativeBuffer<T> array
        )
            where T : unmanaged, IEntityComponent
        {
            outIndex = index;
            if (mapper._map != null && index < mapper._map.Count)
            {
                array = mapper._map.GetValues(out _);
                return true;
            }

            array = default;
            return false;
        }
    }
}
