using System;
using Trecs.Internal;

namespace Trecs
{
    internal class EcsHeapAllocator : IDisposable
    {
        readonly UniqueHeap _uniqueHeap;
        readonly SharedHeap _sharedHeap;
        readonly NativeSharedHeap _nativeSharedHeap;
        readonly FrameScopedUniqueHeap _frameScopedUniqueHeap;
        readonly FrameScopedSharedHeap _frameScopedSharedHeap;
        readonly FrameScopedNativeSharedHeap _nativeFrameScopedSharedHeap;
        readonly NativeUniqueHeap _nativeUniqueHeap;
        readonly FrameScopedNativeUniqueHeap _frameScopedNativeUniqueHeap;
        readonly NativeChunkStore _nativeUniqueChunkStore;
        readonly TrecsListHeap _trecsListHeap;

        bool _isDisposed;

        public EcsHeapAllocator(
            UniqueHeap uniqueHeap,
            SharedHeap sharedHeap,
            NativeSharedHeap nativeSharedHeap,
            FrameScopedUniqueHeap frameScopedUniqueHeap,
            FrameScopedSharedHeap frameScopedSharedHeap,
            FrameScopedNativeSharedHeap nativeFrameScopedSharedHeap,
            NativeUniqueHeap nativeUniqueHeap,
            FrameScopedNativeUniqueHeap frameScopedNativeUniqueHeap,
            NativeChunkStore nativeUniqueChunkStore,
            TrecsListHeap trecsListHeap
        )
        {
            _uniqueHeap = uniqueHeap;
            _sharedHeap = sharedHeap;
            _nativeSharedHeap = nativeSharedHeap;
            _frameScopedUniqueHeap = frameScopedUniqueHeap;
            _frameScopedSharedHeap = frameScopedSharedHeap;
            _nativeFrameScopedSharedHeap = nativeFrameScopedSharedHeap;
            _nativeUniqueHeap = nativeUniqueHeap;
            _frameScopedNativeUniqueHeap = frameScopedNativeUniqueHeap;
            _nativeUniqueChunkStore = nativeUniqueChunkStore;
            _trecsListHeap = trecsListHeap;
        }

        internal UniqueHeap UniqueHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _uniqueHeap;
            }
        }

        internal NativeSharedHeap NativeSharedHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _nativeSharedHeap;
            }
        }

        internal SharedHeap SharedHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _sharedHeap;
            }
        }

        internal FrameScopedUniqueHeap FrameScopedUniqueHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _frameScopedUniqueHeap;
            }
        }

        internal FrameScopedSharedHeap FrameScopedSharedHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _frameScopedSharedHeap;
            }
        }

        internal FrameScopedNativeSharedHeap FrameScopedNativeSharedHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _nativeFrameScopedSharedHeap;
            }
        }

        internal NativeUniqueHeap NativeUniqueHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _nativeUniqueHeap;
            }
        }

        internal FrameScopedNativeUniqueHeap FrameScopedNativeUniqueHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _frameScopedNativeUniqueHeap;
            }
        }

        /// <summary>
        /// Shared paged-slab store backing <see cref="NativeUniqueHeap"/>,
        /// <see cref="FrameScopedNativeUniqueHeap"/>, and <see cref="TrecsListHeap"/>.
        /// Exposed for serialization — the chunk store dumps its full state ahead of the
        /// consuming heaps' managed-side bookkeeping.
        /// </summary>
        internal NativeChunkStore NativeUniqueChunkStore
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _nativeUniqueChunkStore;
            }
        }

        internal TrecsListHeap TrecsListHeap
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return _trecsListHeap;
            }
        }

        internal ref NativeUniquePtrResolver NativeUniquePtrResolver
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return ref _nativeUniqueHeap.Resolver;
            }
        }

        internal ref NativeTrecsListResolver NativeTrecsListResolver
        {
            get
            {
                TrecsAssert.That(!_isDisposed);
                return ref _trecsListHeap.Resolver;
            }
        }

        public void Dispose()
        {
            TrecsAssert.That(!_isDisposed);
            _isDisposed = true;

            _nativeSharedHeap.Dispose();
            // All three native heaps share one chunk store; they must all finish freeing
            // their handles before the chunk store itself can be disposed (anything still
            // allocated at chunk-store dispose is reported as a leak).
            _nativeUniqueHeap.Dispose();
            _frameScopedNativeUniqueHeap.Dispose();
            _trecsListHeap.Dispose();
            _nativeUniqueChunkStore.Dispose();
            _sharedHeap.Dispose();
            _frameScopedUniqueHeap.Dispose();
            _frameScopedSharedHeap.Dispose();
            _nativeFrameScopedSharedHeap.Dispose();
            _uniqueHeap.Dispose();
        }

        /////////

        internal bool TryAllocNativeShared<T>(BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeSharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        internal NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeSharedHeap.GetBlob<T>(blobId);
        }

        internal NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId, in T blob)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeSharedHeap.CreateBlob<T>(blobId, in blob);
        }

        internal SharedPtr<T> AllocShared<T>(BlobId blobId, T blob)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _sharedHeap.CreateBlob<T>(blobId, blob);
        }

        internal bool TryAllocShared<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _sharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        internal SharedPtr<T> AllocShared<T>(BlobId blobId)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _sharedHeap.GetBlob<T>(blobId);
        }

        internal UniquePtr<T> AllocUnique<T>()
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _uniqueHeap.AllocUnique<T>();
        }

        internal UniquePtr<T> AllocUnique<T>(T value)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _uniqueHeap.AllocUnique<T>(value);
        }

        internal UniquePtr<T> AllocUniqueFrameScoped<T>(int frame)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _frameScopedUniqueHeap.Alloc<T>(frame);
        }

        internal UniquePtr<T> AllocUniqueFrameScoped<T>(int frame, T value)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _frameScopedUniqueHeap.Alloc<T>(frame, value);
        }

        internal SharedPtr<T> AllocSharedFrameScoped<T>(int frame, BlobId blobId, T value)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _frameScopedSharedHeap.CreateBlob<T>(frame, blobId, value);
        }

        internal SharedPtr<T> AllocSharedFrameScoped<T>(int frame, BlobId blobId)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _frameScopedSharedHeap.CreateBlob<T>(frame, blobId);
        }

        internal bool TryAllocSharedFrameScoped<T>(int frame, BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            TrecsAssert.That(!_isDisposed);
            return _frameScopedSharedHeap.TryGetBlob<T>(frame, blobId, out ptr);
        }

        internal NativeSharedPtr<T> AllocNativeSharedFrameScoped<T>(
            int frame,
            BlobId blobId,
            in T value
        )
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeFrameScopedSharedHeap.CreateBlob<T>(frame, blobId, in value);
        }

        internal NativeSharedPtr<T> AllocNativeSharedFrameScoped<T>(int frame, BlobId blobId)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeFrameScopedSharedHeap.CreateBlob<T>(frame, blobId);
        }

        internal bool TryAllocNativeSharedFrameScoped<T>(
            int frame,
            BlobId blobId,
            out NativeSharedPtr<T> ptr
        )
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeFrameScopedSharedHeap.TryGetBlob<T>(frame, blobId, out ptr);
        }

        internal TrecsList<T> AllocTrecsList<T>(int initialCapacity)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _trecsListHeap.Alloc<T>(initialCapacity);
        }

        internal NativeUniquePtr<T> AllocNativeUnique<T>(in T value)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeUniqueHeap.Alloc<T>(in value);
        }

        internal NativeUniquePtr<T> AllocNativeUnique<T>()
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeUniqueHeap.Alloc<T>();
        }

        internal NativeUniquePtr<T> AllocNativeUniqueTakingOwnership<T>(NativeBlobAllocation alloc)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeUniqueHeap.AllocTakingOwnership<T>(alloc);
        }

        internal NativeUniquePtr<T> AllocNativeUniqueFrameScoped<T>(int frame, in T value)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _frameScopedNativeUniqueHeap.Alloc<T>(frame, in value);
        }

        internal NativeUniquePtr<T> AllocNativeUniqueFrameScoped<T>(int frame)
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _frameScopedNativeUniqueHeap.Alloc<T>(frame);
        }

        internal NativeUniquePtr<T> AllocNativeUniqueFrameScopedTakingOwnership<T>(
            int frame,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _frameScopedNativeUniqueHeap.AllocTakingOwnership<T>(frame, alloc);
        }

        internal NativeSharedPtr<T> AllocNativeSharedTakingOwnership<T>(
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeSharedHeap.CreateBlobTakingOwnership<T>(blobId, alloc);
        }

        internal NativeSharedPtr<T> AllocNativeSharedFrameScopedTakingOwnership<T>(
            int frame,
            BlobId blobId,
            NativeBlobAllocation alloc
        )
            where T : unmanaged
        {
            TrecsAssert.That(!_isDisposed);
            return _nativeFrameScopedSharedHeap.CreateBlobTakingOwnership<T>(frame, blobId, alloc);
        }
    }
}
