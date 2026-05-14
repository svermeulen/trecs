using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Trecs.Internal;
using Unity.Mathematics;

namespace Trecs
{
    /// <summary>
    /// Pointer to a managed (class) blob stored in the <see cref="BlobCache"/>.
    /// Lives in <see cref="Trecs.Internal"/> because the supported public path
    /// for shared managed data is <see cref="SharedPtr{T}"/> via
    /// <see cref="SharedPtr.Alloc{T}(HeapAccessor, BlobId, T)"/>; <see cref="BlobPtr{T}"/>
    /// is only used by callers writing custom <see cref="IBlobStore"/> backends.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
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
            TrecsAssert.That(!IsNull);
            TrecsAssert.That(
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
            TrecsAssert.That(!IsNull);
            TrecsAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to WarmUp from a disposed BlobPtr"
            );
            blobCache.WarmUpBlob(BlobId);
        }

        public BlobLoadingState GetLoadingState(BlobCache blobCache)
        {
            TrecsAssert.That(!IsNull);
            TrecsAssert.That(
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

            TrecsAssert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to Cast a disposed BlobPtr"
            );

#if DEBUG
            var actualType = blobCache.GetManagedBlobType(BlobId);
            TrecsAssert.That(
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
            TrecsAssert.That(!IsNull);
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
