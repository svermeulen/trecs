using System;
using Trecs.Internal;

namespace Trecs
{
    internal class EcsHeapAllocator : IDisposable
    {
        readonly UniqueHeap _uniqueHeap;
        readonly SharedHeap _sharedHeap;
        readonly NativeSharedHeap _nativeSharedHeap;
        readonly NativeHeap _nativeUniqueChunkStore;
        readonly InputNativeUniqueHeap _inputNativeUniqueHeap;
        readonly InputNativeSharedHeap _inputNativeSharedHeap;
        readonly InputSharedHeap _inputSharedHeap;
        readonly InputUniqueHeap _inputUniqueHeap;

        bool _isDisposed;

        public EcsHeapAllocator(
            UniqueHeap uniqueHeap,
            SharedHeap sharedHeap,
            NativeSharedHeap nativeSharedHeap,
            NativeHeap nativeUniqueChunkStore,
            InputNativeUniqueHeap inputNativeUniqueHeap,
            InputNativeSharedHeap inputNativeSharedHeap,
            InputSharedHeap inputSharedHeap,
            InputUniqueHeap inputUniqueHeap
        )
        {
            _uniqueHeap = uniqueHeap;
            _sharedHeap = sharedHeap;
            _nativeSharedHeap = nativeSharedHeap;
            _nativeUniqueChunkStore = nativeUniqueChunkStore;
            _inputNativeUniqueHeap = inputNativeUniqueHeap;
            _inputNativeSharedHeap = inputNativeSharedHeap;
            _inputSharedHeap = inputSharedHeap;
            _inputUniqueHeap = inputUniqueHeap;
        }

        internal UniqueHeap UniqueHeap
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _uniqueHeap;
            }
        }

        internal NativeSharedHeap NativeSharedHeap
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _nativeSharedHeap;
            }
        }

        internal SharedHeap SharedHeap
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _sharedHeap;
            }
        }

        internal InputNativeUniqueHeap InputNativeUniqueHeap
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _inputNativeUniqueHeap;
            }
        }

        internal InputNativeSharedHeap InputNativeSharedHeap
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _inputNativeSharedHeap;
            }
        }

        internal InputSharedHeap InputSharedHeap
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _inputSharedHeap;
            }
        }

        internal InputUniqueHeap InputUniqueHeap
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _inputUniqueHeap;
            }
        }

        /// <summary>
        /// Shared paged-slab store backing persistent <see cref="NativeUniquePtr{T}"/>
        /// and <see cref="TrecsList{T}"/> allocations. Input-allocated unmanaged
        /// data lives in <see cref="InputNativeUniqueHeap"/>'s own per-frame
        /// arenas (not this store).
        /// </summary>
        internal NativeHeap NativeUniqueChunkStore
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return _nativeUniqueChunkStore;
            }
        }

        internal ref NativeHeapResolver NativeHeapResolver
        {
            get
            {
                TrecsDebugAssert.That(!_isDisposed);
                return ref _nativeUniqueChunkStore.Resolver;
            }
        }

        public void Dispose()
        {
            TrecsDebugAssert.That(!_isDisposed);
            _isDisposed = true;

            _nativeSharedHeap.Dispose();
            _nativeUniqueChunkStore.Dispose();
            _sharedHeap.Dispose();
            _uniqueHeap.Dispose();
            _inputNativeUniqueHeap.Dispose();
            _inputNativeSharedHeap.Dispose();
            _inputSharedHeap.Dispose();
            _inputUniqueHeap.Dispose();
        }

        /////////

        internal bool TryAllocNativeShared<T>(BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _nativeSharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        internal NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _nativeSharedHeap.GetBlob<T>(blobId);
        }

        internal NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId, in T blob)
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _nativeSharedHeap.CreateBlob<T>(blobId, in blob);
        }

        internal SharedPtr<T> AllocShared<T>(BlobId blobId, T blob)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _sharedHeap.CreateBlob<T>(blobId, blob);
        }

        internal bool TryAllocShared<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _sharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        internal SharedPtr<T> AllocShared<T>(BlobId blobId)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _sharedHeap.GetBlob<T>(blobId);
        }

        internal UniquePtr<T> AllocUnique<T>()
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _uniqueHeap.AllocUnique<T>();
        }

        internal UniquePtr<T> AllocUnique<T>(T value)
            where T : class
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _uniqueHeap.AllocUnique<T>(value);
        }

        internal NativeSharedPtr<T> AllocNativeSharedTakingOwnership<T>(
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            TrecsDebugAssert.That(!_isDisposed);
            return _nativeSharedHeap.CreateBlobTakingOwnership<T>(blobId, alloc);
        }
    }
}
