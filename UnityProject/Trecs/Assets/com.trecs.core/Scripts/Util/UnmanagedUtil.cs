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

        public static int BlittableHashCode<T>(in T value)
            where T : unmanaged
        {
            unsafe
            {
                fixed (T* p = &value)
                {
                    return BlittableHashCode(p, sizeof(T));
                }
            }
        }

        public static unsafe int BlittableHashCode(void* data, int sizeInBytes)
        {
            byte* b = (byte*)data;
            uint h = 2166136261u;
            for (int i = 0; i < sizeInBytes; i++)
            {
                h ^= b[i];
                h *= 16777619u;
            }
            return (int)h;
        }
    }
}
