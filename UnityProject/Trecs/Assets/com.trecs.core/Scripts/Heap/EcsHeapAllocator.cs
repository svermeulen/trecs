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

        bool _isDisposed;

        public EcsHeapAllocator(
            UniqueHeap uniqueHeap,
            SharedHeap sharedHeap,
            NativeSharedHeap nativeSharedHeap,
            FrameScopedUniqueHeap frameScopedUniqueHeap,
            FrameScopedSharedHeap frameScopedSharedHeap,
            FrameScopedNativeSharedHeap nativeFrameScopedSharedHeap,
            NativeUniqueHeap nativeUniqueHeap,
            FrameScopedNativeUniqueHeap frameScopedNativeUniqueHeap
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

            _nativeUniqueHeap.SetFrameScopedEntries(_frameScopedNativeUniqueHeap.AllEntries);
        }

        internal UniqueHeap UniqueHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _uniqueHeap;
            }
        }

        internal NativeSharedHeap NativeSharedHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _nativeSharedHeap;
            }
        }

        internal SharedHeap SharedHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _sharedHeap;
            }
        }

        internal FrameScopedUniqueHeap FrameScopedUniqueHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _frameScopedUniqueHeap;
            }
        }

        internal FrameScopedSharedHeap FrameScopedSharedHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _frameScopedSharedHeap;
            }
        }

        internal FrameScopedNativeSharedHeap FrameScopedNativeSharedHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _nativeFrameScopedSharedHeap;
            }
        }

        internal NativeUniqueHeap NativeUniqueHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _nativeUniqueHeap;
            }
        }

        internal FrameScopedNativeUniqueHeap FrameScopedNativeUniqueHeap
        {
            get
            {
                Assert.That(!_isDisposed);
                return _frameScopedNativeUniqueHeap;
            }
        }

        internal ref NativeUniquePtrResolver NativeUniquePtrResolver
        {
            get
            {
                Assert.That(!_isDisposed);
                return ref _nativeUniqueHeap.Resolver;
            }
        }

        public void Dispose()
        {
            Assert.That(!_isDisposed);
            _isDisposed = true;

            _nativeSharedHeap.Dispose();
            _nativeUniqueHeap.Dispose();
            _sharedHeap.Dispose();
            _frameScopedUniqueHeap.Dispose();
            _frameScopedSharedHeap.Dispose();
            _nativeFrameScopedSharedHeap.Dispose();
            _frameScopedNativeUniqueHeap.Dispose();
            _uniqueHeap.Dispose();
        }

        /////////

        internal bool TryAllocNativeShared<T>(BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeSharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        internal NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeSharedHeap.GetBlob<T>(blobId);
        }

        internal NativeSharedPtr<T> AllocNativeShared<T>(BlobId blobId, in T blob)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeSharedHeap.CreateBlob<T>(blobId, in blob);
        }

        internal SharedPtr<T> AllocShared<T>(BlobId blobId, T blob)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _sharedHeap.CreateBlob<T>(blobId, blob);
        }

        internal bool TryAllocShared<T>(BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _sharedHeap.TryGetBlob<T>(blobId, out ptr);
        }

        internal SharedPtr<T> AllocShared<T>(BlobId blobId)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _sharedHeap.GetBlob<T>(blobId);
        }

        internal UniquePtr<T> AllocUnique<T>()
            where T : class
        {
            Assert.That(!_isDisposed);
            return _uniqueHeap.AllocUnique<T>();
        }

        internal UniquePtr<T> AllocUnique<T>(T value)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _uniqueHeap.AllocUnique<T>(value);
        }

        internal UniquePtr<T> AllocUniqueFrameScoped<T>(int frame)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _frameScopedUniqueHeap.Alloc<T>(frame);
        }

        internal UniquePtr<T> AllocUniqueFrameScoped<T>(int frame, T value)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _frameScopedUniqueHeap.Alloc<T>(frame, value);
        }

        internal SharedPtr<T> AllocSharedFrameScoped<T>(int frame, BlobId blobId, T value)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _frameScopedSharedHeap.CreateBlob<T>(frame, blobId, value);
        }

        internal SharedPtr<T> AllocSharedFrameScoped<T>(int frame, BlobId blobId)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _frameScopedSharedHeap.CreateBlob<T>(frame, blobId);
        }

        internal bool TryAllocSharedFrameScoped<T>(int frame, BlobId blobId, out SharedPtr<T> ptr)
            where T : class
        {
            Assert.That(!_isDisposed);
            return _frameScopedSharedHeap.TryGetBlob<T>(frame, blobId, out ptr);
        }

        internal NativeSharedPtr<T> AllocNativeSharedFrameScoped<T>(
            int frame,
            BlobId blobId,
            in T value
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeFrameScopedSharedHeap.CreateBlob<T>(frame, blobId, in value);
        }

        internal NativeSharedPtr<T> AllocNativeSharedFrameScoped<T>(int frame, BlobId blobId)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeFrameScopedSharedHeap.CreateBlob<T>(frame, blobId);
        }

        internal bool TryAllocNativeSharedFrameScoped<T>(
            int frame,
            BlobId blobId,
            out NativeSharedPtr<T> ptr
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeFrameScopedSharedHeap.TryGetBlob<T>(frame, blobId, out ptr);
        }

        internal NativeUniquePtr<T> AllocNativeUnique<T>(in T value)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeUniqueHeap.Alloc<T>(in value);
        }

        internal NativeUniquePtr<T> AllocNativeUnique<T>()
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeUniqueHeap.Alloc<T>();
        }

        internal NativeUniquePtr<T> AllocNativeUniqueTakingOwnership<T>(
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeUniqueHeap.AllocTakingOwnership<T>(ptr, allocSize, allocAlignment);
        }

        internal NativeUniquePtr<T> AllocNativeUniqueFrameScoped<T>(int frame, in T value)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _frameScopedNativeUniqueHeap.Alloc<T>(frame, in value);
        }

        internal NativeUniquePtr<T> AllocNativeUniqueFrameScoped<T>(int frame)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _frameScopedNativeUniqueHeap.Alloc<T>(frame);
        }

        internal NativeUniquePtr<T> AllocNativeUniqueFrameScopedTakingOwnership<T>(
            int frame,
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _frameScopedNativeUniqueHeap.AllocTakingOwnership<T>(
                frame,
                ptr,
                allocSize,
                allocAlignment
            );
        }

        internal NativeSharedPtr<T> AllocNativeSharedTakingOwnership<T>(
            BlobId blobId,
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeSharedHeap.CreateBlobTakingOwnership<T>(
                blobId,
                ptr,
                allocSize,
                allocAlignment
            );
        }

        internal NativeSharedPtr<T> AllocNativeSharedFrameScopedTakingOwnership<T>(
            int frame,
            BlobId blobId,
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            return _nativeFrameScopedSharedHeap.CreateBlobTakingOwnership<T>(
                frame,
                blobId,
                ptr,
                allocSize,
                allocAlignment
            );
        }
    }
}
