using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Read-only view over a single group's entity handle array.
    /// Provides O(1) indexed access to EntityHandles without the per-iteration
    /// dictionary lookup that <see cref="NativeWorldAccessor.GetEntityHandle"/> requires.
    /// Constructed by source-generated job code for <c>[FromWorld] NativeEntityHandleBuffer</c> fields.
    /// </summary>
    public readonly struct NativeEntityHandleBuffer
    {
        readonly NativeBuffer<int> _uniqueIds;
        readonly NativeBuffer<EntityHandleMapElement> _forwardMap;

        internal NativeEntityHandleBuffer(
            NativeBuffer<int> uniqueIds,
            NativeBuffer<EntityHandleMapElement> forwardMap
        )
        {
            _uniqueIds = uniqueIds;
            _forwardMap = forwardMap;
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _uniqueIds.Capacity;
        }

        public EntityHandle this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int uniqueId = _uniqueIds.IndexAsReadOnly(index);
                ref readonly var element = ref _forwardMap.IndexAsReadOnly(uniqueId - 1);
                return new EntityHandle(uniqueId, element.Version);
            }
        }
    }
}
