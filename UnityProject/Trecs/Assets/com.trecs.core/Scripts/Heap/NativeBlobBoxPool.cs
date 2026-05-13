using System;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs.Internal
{
    /// <summary>
    /// Per-world pool of <see cref="NativeBlobBox"/> wrapper instances.
    /// Recycles the managed wrapper so that per-frame native allocations
    /// don't churn the GC. The underlying native memory still goes through
    /// <c>AllocatorManager</c> per Rent/Dispose because allocation sizes vary.
    ///
    /// Main-thread only — the free-list is a plain <see cref="Stack{T}"/>
    /// and <c>AllocatorManager.Allocate/Free</c> are themselves main-thread
    /// contracts. Must outlive every heap and every blob store that holds
    /// rented boxes, since both call <see cref="NativeBlobBox.Dispose"/>
    /// (which returns to the pool) during their own teardown.
    /// </summary>
    public sealed class NativeBlobBoxPool : IDisposable
    {
        readonly Stack<NativeBlobBox> _free = new();
        bool _isDisposed;

        internal unsafe NativeBlobBox RentFromValue<T>(in T value)
            where T : unmanaged
        {
            AssertRentable();
            var size = UnsafeUtility.SizeOf<T>();
            var alignment = UnsafeUtility.AlignOf<T>();
            var box = TakeOrCreate();
            box.Init(size, alignment, typeof(T));
            *(T*)box.Ptr = value;
            return box;
        }

        internal NativeBlobBox RentUninitialized(int size, int alignment, Type innerType)
        {
            AssertRentable();
            var box = TakeOrCreate();
            box.Init(size, alignment, innerType);
            return box;
        }

        internal NativeBlobBox RentTakingOwnership(NativeBlobAllocation alloc, Type innerType)
        {
            AssertRentable();
            var box = TakeOrCreate();
            box.InitFromExistingPointer(alloc, innerType);
            return box;
        }

        void AssertRentable()
        {
            Assert.That(!_isDisposed);
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeBlobBoxPool is main-thread only; the free-list is not thread-safe"
            );
        }

        NativeBlobBox TakeOrCreate()
        {
            return _free.Count > 0 ? _free.Pop() : new NativeBlobBox(this);
        }

        // Called by NativeBlobBox.Dispose after the native free has happened.
        // The native memory is already released, so a stray return after pool
        // dispose doesn't leak anything — but it does mean the teardown order
        // is wrong (pool was torn down while a heap/store still held boxes),
        // so we assert rather than silently absorbing it.
        internal void Return(NativeBlobBox box)
        {
            Assert.That(
                !_isDisposed,
                "Box returned to disposed NativeBlobBoxPool — a heap or blob store outlived its pool"
            );
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeBlobBoxPool is main-thread only; the free-list is not thread-safe"
            );
            _free.Push(box);
        }

        public void Dispose()
        {
            Assert.That(!_isDisposed);
            // Boxes on the free-list have already had their native memory freed.
            // We drop our references and let the GC collect the wrappers.
            _free.Clear();
            _isDisposed = true;
        }
    }
}
