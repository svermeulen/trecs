using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class ComponentBufferExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetRawPointer<T>(
            this NativeComponentBufferRead<T> buffer,
            out int capacity
        )
            where T : unmanaged
        {
            return buffer.GetRawPointer(out capacity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr GetRawPointer<T>(
            this NativeComponentBufferWrite<T> buffer,
            out int capacity
        )
            where T : unmanaged
        {
            return buffer.GetRawPointer(out capacity);
        }
    }
}
