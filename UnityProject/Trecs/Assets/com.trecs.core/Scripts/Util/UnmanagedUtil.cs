using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    public static class UnmanagedUtil
    {
        public static bool BlittableEquals<T>(in T lhs, in T rhs)
            where T : unmanaged
        {
            unsafe
            {
                fixed (T* thisPtr = &lhs)
                fixed (T* otherPtr = &rhs)
                {
                    return UnsafeUtility.MemCmp(thisPtr, otherPtr, sizeof(T)) == 0;
                }
            }
        }
    }
}
