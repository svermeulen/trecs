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
        readonly NativeBuffer<EntityHandle> _nb;

        internal NativeEntityHandleBuffer(NativeBuffer<EntityHandle> nb)
        {
            _nb = nb;
        }

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _nb.Capacity;
        }

        public EntityHandle this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _nb.IndexAsReadOnly(index);
        }
    }
}
