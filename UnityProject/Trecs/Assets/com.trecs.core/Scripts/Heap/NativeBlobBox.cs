using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Trecs.Internal
{
    /// <summary>
    /// Type-erased holder for a single unmanaged value living in native memory.
    /// Owns an allocation made via AllocatorManager.Allocate(Allocator.Persistent).
    ///
    /// Used internally by all Trecs native heaps as the canonical ownership type.
    /// Not intended for direct use outside of Trecs.
    /// </summary>
    internal sealed class NativeBlobBox : IDisposable
    {
        IntPtr _ptr;
        readonly int _size;
        readonly int _alignment;
        readonly Type _innerType;
        bool _disposed;

        public IntPtr Ptr
        {
            get
            {
                Assert.That(!_disposed, "NativeBlobBox used after Dispose");
                return _ptr;
            }
        }
        public int Size => _size;
        public int Alignment => _alignment;
        public Type InnerType => _innerType;
        public bool IsDisposed => _disposed;

        NativeBlobBox(IntPtr ptr, int size, int alignment, Type innerType)
        {
            Assert.That(ptr != IntPtr.Zero);
            Assert.IsNotNull(innerType);
            Assert.That(size > 0);
            Assert.That(
                alignment > 0 && (alignment & (alignment - 1)) == 0,
                "Alignment must be a positive power of two"
            );
            Assert.That(
                (ptr.ToInt64() & (alignment - 1)) == 0,
                "Pointer is not aligned to {} bytes",
                alignment
            );
            _ptr = ptr;
            _size = size;
            _alignment = alignment;
            _innerType = innerType;
            NativeAllocTracker.OnAllocated();
        }

        ~NativeBlobBox()
        {
            if (!_disposed)
            {
                // We don't free here because finalizers run on a non-main thread and
                // AllocatorManager.Free is main-thread only. We just warn so the leak
                // is visible. Unity's NativeLeakDetection (via MallocTracked) catches
                // the underlying allocation too.
                Debug.LogError(
                    $"NativeBlobBox of inner type {_innerType} was leaked (not Disposed)"
                );
            }
        }

        public unsafe void Dispose()
        {
            Assert.That(!_disposed, "NativeBlobBox double-disposed");
            AllocatorManager.Free(
                Allocator.Persistent,
                _ptr.ToPointer(),
                _size,
                _alignment,
                items: 1
            );
            _ptr = IntPtr.Zero;
            _disposed = true;
            NativeAllocTracker.OnDisposed();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Allocates a new NativeBlobBox sized to hold a value of type T and copies the value in.
        /// </summary>
        public static unsafe NativeBlobBox AllocFromValue<T>(in T value)
            where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            var alignment = UnsafeUtility.AlignOf<T>();
            void* ptr = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, items: 1);
            *(T*)ptr = value;
            return new NativeBlobBox(new IntPtr(ptr), size, alignment, typeof(T));
        }

        /// <summary>
        /// Allocates a new uninitialized NativeBlobBox of the given size and alignment.
        /// Used for deserialization paths that fill the bytes manually.
        /// </summary>
        public static unsafe NativeBlobBox AllocUninitialized(
            int size,
            int alignment,
            Type innerType
        )
        {
            void* ptr = AllocatorManager.Allocate(Allocator.Persistent, size, alignment, items: 1);
            return new NativeBlobBox(new IntPtr(ptr), size, alignment, innerType);
        }

        /// <summary>
        /// Wraps an existing native pointer that was allocated via
        /// AllocatorManager.Allocate(Allocator.Persistent, size, alignment, 1).
        /// The caller transfers ownership: the box will free the pointer on Dispose.
        /// </summary>
        public static NativeBlobBox FromExistingPointer(
            IntPtr ptr,
            int size,
            int alignment,
            Type innerType
        )
        {
            return new NativeBlobBox(ptr, size, alignment, innerType);
        }
    }
}
