using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Burst;

namespace Trecs.Internal
{
    //Note: SharedStatic MUST always be initialised outside burst otherwise undefined behaviour will happen
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal struct SharedStaticWrapper<T, Key>
        where T : unmanaged
    {
        static readonly SharedStatic<T> uniqueContextId = SharedStatic<T>.GetOrCreate<Key>();

        public ref T Data
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref uniqueContextId.Data;
        }
    }
}
