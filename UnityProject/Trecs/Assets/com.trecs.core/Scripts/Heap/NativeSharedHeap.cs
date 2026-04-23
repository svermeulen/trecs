using System;
using System.Collections.Generic;
using System.Linq;
using Trecs.Collections;
using Trecs.Internal;
using Unity.Collections;

namespace Trecs
{
    /// <summary>
    /// Manages reference-counted native (unmanaged) allocations backing <see cref="NativeSharedPtr{T}"/>.
    /// Provides a <see cref="NativeSharedPtrResolver"/> for Burst-compatible pointer resolution in jobs.
    /// Accessed internally through <see cref="HeapAccessor"/>; not typically used directly.
    ///
    /// <para><b>Pending-flush invariant.</b> New blobs created via <c>CreateBlob</c> are staged in
    /// <c>_pendingAdds</c> and do not appear in <c>_allEntries</c> (the table that
    /// <see cref="NativeSharedPtrResolver"/> reads from) until <c>FlushPendingOperations</c> runs
    /// at the next submission boundary. Consequences for callers:</para>
    /// <list type="bullet">
    ///   <item><description><c>ResolveUnsafePtr</c> on the main thread (e.g. via <c>HeapAccessor</c>)
    ///     transparently checks <c>_pendingAdds</c> and works on freshly-created blobs.</description></item>
    ///   <item><description><see cref="NativeSharedPtrResolver"/> inside a Burst job only sees flushed
    ///     entries. Scheduling a job that resolves a <c>NativeSharedPtr</c> created in the same frame
    ///     before submission will fail at resolution time. Schedule such jobs after submission or defer
    ///     creation until the previous frame.</description></item>
    /// </list>
    /// </summary>
    public class NativeSharedHeap
    {
        static readonly TrecsLog _log = new(nameof(NativeSharedHeap));

        readonly BlobCache _store;
        readonly DenseDictionary<BlobId, PtrHandle> _blobCacheHandles = new();
        readonly NativeDenseDictionary<BlobId, NativeSharedHeapEntry> _allEntries;
        readonly DenseDictionary<BlobId, BlobInfo> _activeBlobs = new();
        readonly DenseDictionary<PtrHandle, BlobId> _activeHandles = new();
        readonly List<PtrHandle> _tempBuffer1 = new();
        readonly List<(BlobId blobId, PtrHandle cacheHandle)> _pendingRemoves = new();
        readonly Dictionary<BlobId, NativeSharedHeapEntry> _pendingAdds = new();

        readonly HeapIdCounter _idCounter = new(1, 2);
        bool _isDisposed;
        NativeSharedPtrResolver _resolver;

        public NativeSharedHeap(BlobCache store)
        {
            _store = store;

            _allEntries = new NativeDenseDictionary<BlobId, NativeSharedHeapEntry>(
                1,
                Allocator.Persistent
            );
            _resolver = new NativeSharedPtrResolver(_allEntries);
        }

        public int NumEntries
        {
            get
            {
                Assert.That(!_isDisposed);
                return _activeBlobs.Count;
            }
        }

        public ref NativeSharedPtrResolver Resolver
        {
            get
            {
                Assert.That(!_isDisposed);
                return ref _resolver;
            }
        }

        /// <summary>
        /// Number of newly-created blobs staged for insertion into
        /// <c>_allEntries</c> at the next <c>FlushPendingOperations</c>. While
        /// non-zero, Burst jobs that resolve those specific blobs via
        /// <see cref="NativeSharedPtrResolver"/> will fail. The submission
        /// pipeline flushes before scheduling native submission jobs, so the
        /// common case is handled automatically — this property is useful for
        /// user code that wants to schedule its own jobs mid-frame and needs
        /// to check the invariant itself.
        /// </summary>
        public int PendingAddCount => _pendingAdds.Count;

        /// <summary>
        /// Resolves a blob pointer from managed (main-thread) code.
        /// Unlike NativeSharedPtrResolver, this can resolve blobs that were just created
        /// in the current frame (before FlushPendingOperations).
        /// </summary>
        internal unsafe void* ResolveUnsafePtr<T>(BlobId address)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            Assert.That(
                UnityThreadHelper.IsMainThread,
                "NativeSharedHeap.ResolveUnsafePtr (and ISharedPtrResolver) is main-thread only. "
                    + "Burst jobs must use NativeSharedPtrResolver instead."
            );
            Assert.That(!address.IsNull, "Attempted to resolve null blob address");

            if (_pendingAdds.TryGetValue(address, out var entry))
            {
                if (entry.TypeHash != TypeHash<T>.Value)
                {
                    throw new TrecsException(
                        $"Type hash mismatch resolving NativeSharedPtr<{typeof(T).Name}>: "
                            + $"stored hash {entry.TypeHash}, requested {TypeHash<T>.Value}"
                    );
                }

                return entry.Ptr.ToPointer();
            }

            return _resolver.ResolveUnsafePtr<T>(address);
        }

        unsafe NativeSharedPtr<T> AddBlobEntry<T>(BlobId blobId, PtrHandle blobCacheHandleId)
            where T : unmanaged
        {
            var burstTypeHash = TypeHash<T>.Value;

            _activeBlobs.Add(
                blobId,
                new BlobInfo
                {
                    RefCount = 0,
                    InnerTypeId = TypeIdProvider.GetTypeId<T>(),
                    BurstTypeHash = burstTypeHash,
                }
            );

            _blobCacheHandles.Add(blobId, blobCacheHandleId);
            _log.Trace("Added new blob {}", blobId);

            // Defer adding to _allEntries until FlushPendingOperations,
            // since jobs may be reading _allEntries via NativeSharedPtrResolver
            var ptr = _store.GetNativeBlobPtr(blobId, TypeIdProvider.GetTypeId<T>());
            var entry = new NativeSharedHeapEntry(burstTypeHash, ptr);
            _pendingAdds.Add(blobId, entry);

            return AddBlobHandle<T>(blobId);
        }

        /// <summary>
        /// Creates a new blob and returns a handle to it.
        /// The blob is immediately resolvable from managed code (via WorldAccessor),
        /// but will not be resolvable via NativeSharedPtrResolver (in Burst jobs)
        /// until FlushPendingOperations is called (at submission time).
        /// </summary>
        public NativeSharedPtr<T> CreateBlob<T>(in T blob)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            var handle = _store.CreateNativeBlobPtr(in blob);
            return AddBlobEntry<T>(handle.BlobId, handle.Handle);
        }

        /// <summary>
        /// Creates a new blob with a specific BlobId and returns a handle to it.
        /// The blob is immediately resolvable from managed code (via WorldAccessor),
        /// but will not be resolvable via NativeSharedPtrResolver (in Burst jobs)
        /// until FlushPendingOperations is called (at submission time).
        /// </summary>
        public NativeSharedPtr<T> CreateBlob<T>(BlobId blobId, in T blob)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            var handle = _store.CreateNativeBlobPtr(blobId, in blob);
            return AddBlobEntry<T>(blobId, handle.Handle);
        }

        /// <summary>
        /// Takes ownership of an existing native pointer and creates a blob from it without copying.
        /// See <see cref="NativeUniqueHeap.AllocTakingOwnership{T}"/> for the ownership contract.
        /// </summary>
        public NativeSharedPtr<T> CreateBlobTakingOwnership<T>(
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            var handle = _store.CreateNativeBlobPtrTakingOwnership<T>(
                ptr,
                allocSize,
                allocAlignment
            );
            return AddBlobEntry<T>(handle.BlobId, handle.Handle);
        }

        public NativeSharedPtr<T> CreateBlobTakingOwnership<T>(
            BlobId blobId,
            IntPtr ptr,
            int allocSize,
            int allocAlignment
        )
            where T : unmanaged
        {
            Assert.That(!_isDisposed);
            var handle = _store.CreateNativeBlobPtrTakingOwnership<T>(
                blobId,
                ptr,
                allocSize,
                allocAlignment
            );
            return AddBlobEntry<T>(blobId, handle.Handle);
        }

        /// <summary>
        /// Looks up or loads a blob by BlobId and returns a handle to it.
        /// If a new blob entry is created, it is immediately resolvable from managed code
        /// (via WorldAccessor), but will not be resolvable via NativeSharedPtrResolver (in
        /// Burst jobs) until FlushPendingOperations is called (at submission time).
        /// </summary>
        public bool TryGetBlob<T>(BlobId blobId, out NativeSharedPtr<T> ptr)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);

            _log.Trace("Looking up native blob with id {}", blobId);

            if (_activeBlobs.ContainsKey(blobId))
            {
                ptr = AddBlobHandle<T>(blobId);
                return true;
            }

            if (_store.HasNativeBlob<T>(blobId, updateAccessTime: true))
            {
                var blobCacheHandleId = _store.CreateHandle(blobId);
                ptr = AddBlobEntry<T>(blobId, blobCacheHandleId);
                return true;
            }

            ptr = default;
            return false;
        }

        public NativeSharedPtr<T> GetBlob<T>(BlobId blobId)
            where T : unmanaged
        {
            if (!TryGetBlob<T>(blobId, out var ptr))
            {
                throw Assert.CreateException("Attempted to get an unrecognized blob {}", blobId);
            }

            return ptr;
        }

        NativeSharedPtr<T> AddBlobHandle<T>(BlobId blobId)
            where T : unmanaged
        {
            ref var info = ref _activeBlobs.GetValueByRef(blobId);
            Assert.That(info.InnerTypeId == TypeIdProvider.GetTypeId<T>());
            Assert.That(info.BurstTypeHash == TypeHash<T>.Value);
            info.RefCount += 1;

            var newHandle = new PtrHandle(_idCounter.Alloc());
            _activeHandles.Add(newHandle, blobId);
            _log.Trace("Added blob handle {}", newHandle);

            return new NativeSharedPtr<T>(newHandle, blobId);
        }

        public bool TryClone<T>(PtrHandle handle, out NativeSharedPtr<T> result)
            where T : unmanaged
        {
            Assert.That(!_isDisposed);

            if (!_activeHandles.TryGetValue(handle, out var blobId))
            {
                result = default;
                return false;
            }

            result = AddBlobHandle<T>(blobId);
            return true;
        }

        public void ClearAll(bool warnUndisposed)
        {
            Assert.That(!_isDisposed);
            if (_activeHandles.Count > 0)
            {
                if (warnUndisposed)
                {
                    if (_log.IsWarningEnabled())
                    {
                        var debugStrings = new HashSet<Type>();

                        foreach (var (handle, blobId) in _activeHandles)
                        {
                            debugStrings.Add(
                                _store.TryGetNativeBlobType(blobId, updateAccessTime: true)
                            );
                        }

                        _log.Warning(
                            "Found {} native blob handles that were not disposed, with types: {l}",
                            _activeHandles.Count,
                            debugStrings.Select(x => x.GetPrettyName()).Join(", ")
                        );
                    }
                }

                var removeQueue = _tempBuffer1;
                removeQueue.Clear();

                foreach (var (handle, _) in _activeHandles)
                {
                    removeQueue.Add(handle);
                }

                foreach (var handle in removeQueue)
                {
                    DisposeHandle(handle);
                }

                _tempBuffer1.Clear();
            }

            // AddBlobEntry and DisposeHandle buffer changes — flush them now
            // (safe because ClearAll is only called when no jobs are running)
            FlushPendingOperations();

            Assert.That(_activeBlobs.Count == 0);
            Assert.That(_activeHandles.Count == 0);
            Assert.That(_blobCacheHandles.Count == 0);
            Assert.That(_allEntries.Count == 0);
        }

        /// <summary>
        /// Applies deferred _allEntries additions and removals, plus blob cache handle disposals.
        /// Must be called when no jobs are reading from _allEntries (e.g. at submission time).
        /// </summary>
        internal void FlushPendingOperations()
        {
            if (_pendingAdds.Count > 0)
            {
                _allEntries.EnsureCapacity(_allEntries.Count + _pendingAdds.Count);
                foreach (var (blobId, entry) in _pendingAdds)
                {
                    _allEntries.Add(blobId, entry);
                }
                _pendingAdds.Clear();
            }

            foreach (var (blobId, cacheHandle) in _pendingRemoves)
            {
                _allEntries.Remove(blobId);
                _store.DisposeHandle(cacheHandle);
            }
            _pendingRemoves.Clear();
        }

        internal void Dispose()
        {
            Assert.That(!_isDisposed);
            ClearAll(warnUndisposed: true);

            _allEntries.Dispose();

            _isDisposed = true;
        }

        public void DisposeHandle(in PtrHandle id)
        {
            Assert.That(!_isDisposed);

            if (!_activeHandles.TryGetValue(id, out var blobId))
            {
                throw Assert.CreateException(
                    "Attempted to dispose unrecognized native shared blob handle {} "
                        + "(double-dispose or handle from a different heap?)",
                    id
                );
            }

            _activeHandles.RemoveMustExist(id);
            _log.Trace("Disposed blob handle {}", id);

            ref var info = ref _activeBlobs.GetValueByRef(blobId);
            info.RefCount -= 1;

            Assert.That(info.RefCount >= 0);

            if (info.RefCount == 0)
            {
                _activeBlobs.RemoveMustExist(blobId);

                var blobHandle = _blobCacheHandles.RemoveAndGet(blobId);

                // Defer removing from _allEntries and disposing the cache handle until
                // submission time, since jobs may be reading _allEntries via NativeSharedPtrResolver.
                // Remove uses swap-back which would corrupt concurrent reads.
                _pendingRemoves.Add((blobId, blobHandle));
            }
        }

        public void Serialize(ITrecsSerializationWriter writer)
        {
            FlushPendingOperations();
            Assert.That(_allEntries.Count == _activeBlobs.Count);

            writer.Write<int>("NumEntries", _allEntries.Count);
            writer.Write<uint>("HandleCounter", _idCounter.Value);
            writer.Write<DenseDictionary<BlobId, BlobInfo>>("ActiveBlobs", _activeBlobs);
            writer.Write<DenseDictionary<PtrHandle, BlobId>>("ActiveHandles", _activeHandles);

            _log.Trace("Serialized {} native blob handles", _activeHandles.Count);
        }

        public void Deserialize(ITrecsSerializationReader reader)
        {
            Assert.That(_allEntries.Count == 0);
            Assert.That(_pendingAdds.Count == 0);
            Assert.That(_pendingRemoves.Count == 0);
            Assert.That(_activeBlobs.Count == 0);
            Assert.That(_activeHandles.Count == 0);

            var numEntries = reader.Read<int>("NumEntries");

            _idCounter.Value = reader.Read<uint>("HandleCounter");
            reader.ReadInPlace<DenseDictionary<BlobId, BlobInfo>>("ActiveBlobs", _activeBlobs);
            reader.ReadInPlace<DenseDictionary<PtrHandle, BlobId>>("ActiveHandles", _activeHandles);

            _log.Debug("Deserialized {} native blob handles", _activeHandles.Count);

            Assert.IsEqual(_activeBlobs.Count, numEntries);

            foreach (var (blobId, info) in _activeBlobs)
            {
                var ptr = _store.GetNativeBlobPtr(blobId, info.InnerTypeId, updateAccessTime: true);
                _allEntries.Add(blobId, new NativeSharedHeapEntry(info.BurstTypeHash, ptr));
                _blobCacheHandles.Add(blobId, _store.CreateHandle(blobId));
            }
        }

        public struct BlobInfo
        {
            public int RefCount;
            public int InnerTypeId;
            public int BurstTypeHash;
        }
    }
}
