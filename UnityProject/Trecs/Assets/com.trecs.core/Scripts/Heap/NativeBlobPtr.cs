using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace Trecs.Internal
{
    /// <summary>
    /// Pointer to a native (unmanaged) blob stored in the <see cref="BlobCache"/>.
    /// Resolves to a <c>ref T</c> for direct access. Lives in
    /// <see cref="Trecs.Internal"/> because the supported public path for shared
    /// native data is <see cref="NativeSharedPtr{T}"/> via
    /// <see cref="HeapAccessor.AllocNativeShared{T}(BlobId, in T)"/>;
    /// <see cref="NativeBlobPtr{T}"/> is only used by callers writing custom
    /// <see cref="IBlobStore"/> backends.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct NativeBlobPtr<T> : IEquatable<NativeBlobPtr<T>>, IBlobPtr
        where T : unmanaged
    {
        public readonly PtrHandle Handle;
        public readonly BlobId BlobId;

        public NativeBlobPtr(PtrHandle handle, BlobId blobId)
        {
            Handle = handle;
            BlobId = blobId;
        }

        public static readonly NativeBlobPtr<T> Null = default;

        public readonly bool IsNull
        {
            get { return Handle.IsNull && BlobId == default; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get(BlobCache blobCache)
        {
            Assert.That(!IsNull);
            Assert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to Get from a disposed NativeBlobPtr"
            );
            return ref blobCache.GetNativeBlobRef<T>(BlobId, updateAccessTime: false);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetPtr(BlobCache blobCache, out IntPtr ptr)
        {
            if (IsNull || !blobCache.ContainsHandle(Handle))
            {
                ptr = IntPtr.Zero;
                return false;
            }
            return blobCache.TryGetNativeBlobPtr<T>(BlobId, out ptr, updateAccessTime: false);
        }

        public bool CanGet(BlobCache blobCache)
        {
            if (IsNull)
                return false;
            return blobCache.ContainsHandle(Handle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NativeBlobPtr<T> Clone(BlobCache blobCache)
        {
            if (IsNull)
                return Null;
            var newHandle = blobCache.CreateHandle(BlobId);
            return new NativeBlobPtr<T>(newHandle, BlobId);
        }

        public void WarmUp(BlobCache blobCache)
        {
            Assert.That(!IsNull);
            Assert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to WarmUp from a disposed NativeBlobPtr"
            );
            blobCache.WarmUpBlob(BlobId);
        }

        public BlobLoadingState GetLoadingState(BlobCache blobCache)
        {
            Assert.That(!IsNull);
            Assert.That(
                blobCache.ContainsHandle(Handle),
                "Attempted to GetLoadingState from a disposed NativeBlobPtr"
            );
            return blobCache.GetBlobLoadingState(BlobId);
        }

        public readonly void Dispose(BlobCache blobCache)
        {
            Assert.That(!IsNull);
            blobCache.DisposeHandle(Handle);
        }

        public bool Equals(NativeBlobPtr<T> other)
        {
            return Handle.Equals(other.Handle) && BlobId.Equals(other.BlobId);
        }

        public override bool Equals(object obj)
        {
            return obj is NativeBlobPtr<T> other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((int)math.hash(new int2(Handle.GetHashCode(), BlobId.GetHashCode())));
        }
    }
}
