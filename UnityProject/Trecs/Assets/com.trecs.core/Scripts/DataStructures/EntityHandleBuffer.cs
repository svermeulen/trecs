using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Read-only view over a single group's entity handle array.
    /// Provides O(1) indexed access to EntityHandles without the per-iteration
    /// dictionary lookup that <see cref="NativeWorldAccessor.GetEntityHandle"/> requires.
    /// Constructed by source-generated job code for <c>[FromWorld] NativeEntityHandleBuffer</c> fields.
    /// <para>
    /// <b>Length semantics:</b> <see cref="Length"/> is the high-water mark of the
    /// underlying reverse-map list, not the live entity count — the list grows to
    /// accommodate the largest index ever used and does not shrink on removal. If you
    /// scan past the live prefix, cleared slots return a <see cref="EntityHandle.IsNull"/>
    /// handle. Drive iteration from a component buffer's length (or a count you control)
    /// rather than from <see cref="Length"/> when you need the live count exactly.
    /// </para>
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
                // The reverse-map list is grow-only: removed slots are zeroed but the
                // list length is the high-water mark, not the live count. Return a null
                // handle for cleared slots so callers that scan past the live prefix
                // (e.g., using Length as an upper bound) don't read past the forward map.
                if (uniqueId == 0)
                {
                    return default;
                }
                ref readonly var element = ref _forwardMap.IndexAsReadOnly(uniqueId - 1);
                return new EntityHandle(uniqueId, element.Version);
            }
        }
    }
}
