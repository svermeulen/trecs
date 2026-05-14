using System.Runtime.CompilerServices;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// Read-only view over a single group's entity handle array.
    /// Provides O(1) indexed access to EntityHandles without the per-iteration
    /// dictionary lookup that <see cref="EntityIndex.ToHandle(NativeWorldAccessor)"/> requires.
    /// Constructed by source-generated job code for <c>[FromWorld] NativeEntityHandleBuffer</c> fields.
    /// <para>
    /// <see cref="Length"/> equals the group's live entity count. Every index in
    /// <c>[0, Length)</c> returns a valid, non-null <see cref="EntityHandle"/>.
    /// </para>
    /// </summary>
    public readonly struct NativeEntityHandleBuffer
    {
        readonly NativeBuffer<int> _ids;
        readonly NativeBuffer<EntityHandleMapElement> _forwardMap;

        internal NativeEntityHandleBuffer(
            NativeBuffer<int> ids,
            NativeBuffer<EntityHandleMapElement> forwardMap
        )
        {
            _ids = ids;
            _forwardMap = forwardMap;
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _ids.Length;
        }

        public EntityHandle this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                int id = _ids.IndexAsReadOnly(index);
                ref readonly var element = ref _forwardMap.IndexAsReadOnly(id - 1);
                return new EntityHandle(id, element.Version);
            }
        }
    }
}
