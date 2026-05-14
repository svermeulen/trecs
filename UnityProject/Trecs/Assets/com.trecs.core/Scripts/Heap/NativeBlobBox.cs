using System;
using Unity.Collections;
using UnityEngine;

namespace Trecs.Internal
{
    /// <summary>
    /// Type-erased holder for a single unmanaged value living in native memory.
    /// Owns an allocation made via AllocatorManager.Allocate(Allocator.Persistent).
    ///
    /// Instances are rented from and returned to a <see cref="NativeBlobBoxPool"/>
    /// (one per world) so that per-frame allocations don't churn the GC. Direct
    /// construction is forbidden — all entry points go through the pool.
    ///
    /// Used internally by all Trecs native heaps as the canonical ownership type.
    /// Not intended for direct use outside of Trecs.
    /// </summary>
    internal sealed class NativeBlobBox : IDisposable
    {
        readonly NativeBlobBoxPool _pool;
        IntPtr _ptr;
        int _size;
        int _alignment;
        Type _innerType;

        public IntPtr Ptr => _ptr;
        public int Size => _size;
        public int Alignment => _alignment;
        public Type InnerType => _innerType;
        public bool IsDisposed => _ptr == IntPtr.Zero;

        internal NativeBlobBox(NativeBlobBoxPool pool)
        {
            TrecsAssert.IsNotNull(pool);
            _pool = pool;
        }

        internal unsafe void Init(int size, int alignment, Type innerType)
        {
            AssertCleanState();
            AssertInitArgs(size, alignment, innerType);
            var addr = new IntPtr(
                AllocatorManager.Allocate(Allocator.Persistent, size, alignment, items: 1)
            );
            TrecsAssert.That(
                addr != IntPtr.Zero && (addr.ToInt64() & (alignment - 1)) == 0,
                "AllocatorManager returned a null or misaligned pointer (alignment {0})",
                alignment
            );
            _ptr = addr;
            _size = size;
            _alignment = alignment;
            _innerType = innerType;
        }

        internal void InitFromExistingPointer(NativeBlobAllocation alloc, Type innerType)
        {
            AssertCleanState();
            TrecsAssert.That(alloc.Ptr != IntPtr.Zero);
            AssertInitArgs(alloc.AllocSize, alloc.Alignment, innerType);
            TrecsAssert.That(
                (alloc.Ptr.ToInt64() & (alloc.Alignment - 1)) == 0,
                "Pointer is not aligned to {0} bytes",
                alloc.Alignment
            );
            _ptr = alloc.Ptr;
            _size = alloc.AllocSize;
            _alignment = alloc.Alignment;
            _innerType = innerType;
        }

        void AssertCleanState()
        {
            TrecsAssert.That(
                _ptr == IntPtr.Zero,
                "NativeBlobBox re-initialized while still holding an allocation (inner type {0})",
                _innerType
            );
        }

        static void AssertInitArgs(int size, int alignment, Type innerType)
        {
            TrecsAssert.IsNotNull(innerType);
            TrecsAssert.That(size > 0);
            TrecsAssert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "Alignment must be a positive power of two"
            );
        }

        ~NativeBlobBox()
        {
            if (_ptr != IntPtr.Zero)
            {
                // Finalizers run on a non-main thread and AllocatorManager.Free
                // is main-thread only, so we can't free here — we just warn.
                // Unity's NativeLeakDetection (via MallocTracked on the
                // underlying AllocatorManager.Allocate) is the authoritative
                // leak report; this log just attributes the leak to a
                // NativeBlobBox of a specific inner type.
                Debug.LogError(
                    $"NativeBlobBox of inner type {_innerType} was leaked (not Disposed)"
                );
            }
        }

        public unsafe void Dispose()
        {
            TrecsAssert.That(_ptr != IntPtr.Zero, "NativeBlobBox double-disposed");
            AllocatorManager.Free(
                Allocator.Persistent,
                _ptr.ToPointer(),
                _size,
                _alignment,
                items: 1
            );
            _ptr = IntPtr.Zero;
            _size = 0;
            _alignment = 0;
            _innerType = null;
            // Don't SuppressFinalize — the box is going back on the free-list
            // and may be rented again, so its finalizer must still fire on a
            // future leak.
            _pool.Return(this);
        }
    }
}
