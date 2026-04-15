using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Trecs.Internal
{
    //Note: SharedStatic MUST always be initialised outside burst otherwise undefined behaviour will happen
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct SharedStaticWrapper<T, Key>
        where T : unmanaged
    {
        static readonly Unity.Burst.SharedStatic<T> uniqueContextId =
            Unity.Burst.SharedStatic<T>.GetOrCreate<Key>();

        public ref T Data
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref uniqueContextId.Data;
        }
    }
}
