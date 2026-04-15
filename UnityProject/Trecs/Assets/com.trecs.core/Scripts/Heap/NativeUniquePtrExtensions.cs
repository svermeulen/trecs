using System.Runtime.CompilerServices;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    public static class NativeUniquePtrExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ref T GetMut<T>(
            ref this NativeUniquePtr<T> self,
            in NativeUniquePtrResolver resolver
        )
            where T : unmanaged
        {
            return ref UnsafeUtility.AsRef<T>(resolver.ResolveUnsafePtr<T>(self.Handle.Value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ref T GetMut<T>(ref this NativeUniquePtr<T> self, HeapAccessor heap)
            where T : unmanaged
        {
            return ref UnsafeUtility.AsRef<T>(
                heap.NativeUniqueHeap.ResolveUnsafePtr<T>(self.Handle.Value)
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ref T GetMut<T>(
            ref this NativeUniquePtr<T> self,
            WorldAccessor accessor
        )
            where T : unmanaged
        {
            return ref self.GetMut(accessor.Heap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Set<T>(
            ref this NativeUniquePtr<T> self,
            in NativeUniquePtrResolver resolver,
            in T value
        )
            where T : unmanaged
        {
            ref var target = ref UnsafeUtility.AsRef<T>(
                resolver.ResolveUnsafePtr<T>(self.Handle.Value)
            );
            target = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Set<T>(
            ref this NativeUniquePtr<T> self,
            HeapAccessor heap,
            in T value
        )
            where T : unmanaged
        {
            ref var target = ref UnsafeUtility.AsRef<T>(
                heap.NativeUniqueHeap.ResolveUnsafePtr<T>(self.Handle.Value)
            );
            target = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Set<T>(
            ref this NativeUniquePtr<T> self,
            WorldAccessor accessor,
            in T value
        )
            where T : unmanaged
        {
            self.Set(accessor.Heap, in value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ref T GetMut<T>(
            ref this NativeUniquePtr<T> self,
            in NativeWorldAccessor accessor
        )
            where T : unmanaged
        {
            return ref UnsafeUtility.AsRef<T>(
                accessor.UniquePtrResolver.ResolveUnsafePtr<T>(self.Handle.Value)
            );
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Set<T>(
            ref this NativeUniquePtr<T> self,
            in NativeWorldAccessor accessor,
            in T value
        )
            where T : unmanaged
        {
            ref var target = ref UnsafeUtility.AsRef<T>(
                accessor.UniquePtrResolver.ResolveUnsafePtr<T>(self.Handle.Value)
            );
            target = value;
        }
    }
}
