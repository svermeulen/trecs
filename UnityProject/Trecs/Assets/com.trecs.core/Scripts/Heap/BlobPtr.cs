using System;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Static factories for <see cref="BlobPtr{T}"/>. Per-instance operations
    /// (<c>Get</c>, <c>TryGet</c>, <c>CanGet</c>, <c>Clone</c>, <c>Dispose</c>) live on
    /// the struct itself.
    /// </summary>
    public static class BlobPtr
    {
        /// <summary>
        /// Allocates a new managed blob under <paramref name="blobId"/> in
        /// <paramref name="blobCache"/> and returns a pinning
        /// <see cref="BlobPtr{T}"/>. Fails if a blob already exists at this id.
        /// This is the <see cref="BlobCache"/>-layer counterpart to
        /// <see cref="SharedPtr.Alloc{T}(WorldAccessor, BlobId, T)"/> — use it when the
        /// caller's anchor lifetime is independent of any ECS refcount (e.g. startup
        /// seeders, async preloaders, custom <see cref="IBlobStore"/> backends).
        /// </summary>
        public static BlobPtr<T> Alloc<T>(BlobCache blobCache, BlobId blobId, T value)
            where T : class
        {
            return blobCache.AllocManagedBlob<T>(blobId, value);
        }

        /// <summary>
        /// Returns a fresh pinning <see cref="BlobPtr{T}"/> for the existing managed
        /// blob at <paramref name="blobId"/>, throwing if no such blob exists. The
        /// lookup-only counterpart to <see cref="Alloc{T}(BlobCache, BlobId, T)"/>.
        /// </summary>
        public static BlobPtr<T> Acquire<T>(BlobCache blobCache, BlobId blobId)
            where T : class
        {
            return blobCache.AcquireBlobPtr<T>(blobId);
        }

        /// <summary>
        /// Returns true and a fresh pinning <see cref="BlobPtr{T}"/> if a managed blob
        /// exists at <paramref name="blobId"/>; otherwise false.
        /// </summary>
        public static bool TryGet<T>(BlobCache blobCache, BlobId blobId, out BlobPtr<T> ptr)
            where T : class
        {
            return blobCache.TryAcquireBlobPtr<T>(blobId, out ptr);
        }
    }

    /// <summary>
    /// Lower-level pinning pointer for a managed (class) blob in <see cref="BlobCache"/>.
    /// Most game code should use <see cref="SharedPtr{T}"/> via
    /// <see cref="SharedPtr.Alloc{T}(WorldAccessor, BlobId, T)"/> — that adds the ECS-side
    /// refcount layer on top of the cache. Reach for <see cref="BlobPtr{T}"/> directly
    /// when you need to pin blob bytes outside the ECS refcount lifetime — for example,
    /// startup seeders that anchor blobs before any entity references them, async
    /// preload from a non-ECS subsystem, or when writing a custom
    /// <see cref="IBlobStore"/> backend.
    /// </summary>
    public readonly struct BlobPtr<T> : IEquatable<BlobPtr<T>>, IBlobPtr
        where T : class
    {
        public readonly PtrHandle Handle;
        public readonly BlobId BlobId;

        public BlobPtr(PtrHandle handle, BlobId blobId)
        {
            Handle = handle;
            BlobId = blobId;
        }

        public static readonly BlobPtr<T> Null = default;

        public readonly bool IsNull
        {
            get { return Handle.IsNull && BlobId == default; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get(BlobCache blobCache)
        {
            TrecsDebugAssert.That(!IsNull);
            TrecsDebugAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to Get from a disposed BlobPtr"
            );
            return blobCache.GetManagedBlob<T>(BlobId, updateAccessTime: false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(BlobCache blobCache, out T value)
        {
            if (IsNull || !blobCache.ContainsHandle(Handle))
            {
                value = null;
                return false;
            }
            return blobCache.TryGetManagedBlob<T>(BlobId, out value, updateAccessTime: false);
        }

        public bool CanGet(BlobCache blobCache)
        {
            if (IsNull)
                return false;
            return blobCache.ContainsHandle(Handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BlobPtr<T> Clone(BlobCache blobCache)
        {
            if (IsNull)
                return Null;
            var newHandle = blobCache.CreateHandle(BlobId);
            return new BlobPtr<T>(newHandle, BlobId);
        }

        public void WarmUp(BlobCache blobCache)
        {
            TrecsDebugAssert.That(!IsNull);
            TrecsDebugAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to WarmUp from a disposed BlobPtr"
            );
            blobCache.WarmUpBlob(BlobId);
        }

        public BlobLoadingState GetLoadingState(BlobCache blobCache)
        {
            TrecsDebugAssert.That(!IsNull);
            TrecsDebugAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to GetLoadingState from a disposed BlobPtr"
            );
            return blobCache.GetBlobLoadingState(BlobId);
        }

        public BlobPtr<TTarget> Cast<TTarget>(BlobCache blobCache)
            where TTarget : class
        {
            if (IsNull)
            {
                return BlobPtr<TTarget>.Null;
            }

            TrecsDebugAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to Cast a disposed BlobPtr"
            );

#if DEBUG
            var actualType = blobCache.GetManagedBlobType(BlobId);
            TrecsDebugAssert.That(
                typeof(TTarget).IsAssignableFrom(actualType),
                "BlobPtr cast failed: expected blob assignable to type {0} but found type {1}",
                typeof(TTarget),
                actualType
            );
#endif
            return new BlobPtr<TTarget>(Handle, BlobId);
        }

        public readonly void Dispose(BlobCache blobCache)
        {
            TrecsDebugAssert.That(!IsNull);
            blobCache.DisposeHandle(Handle);
        }

        public bool Equals(BlobPtr<T> other)
        {
            return Handle.Equals(other.Handle) && BlobId.Equals(other.BlobId);
        }

        public override bool Equals(object obj)
        {
            return obj is BlobPtr<T> other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((int)math.hash(new int2(Handle.GetHashCode(), BlobId.GetHashCode())));
        }
    }
}
