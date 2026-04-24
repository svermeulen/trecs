namespace Trecs
{
    /// <summary>
    /// Common interface for typed blob pointers (<see cref="BlobPtr{T}"/> and
    /// <see cref="NativeBlobPtr{T}"/>), providing nullability checks, warm-up,
    /// asynchronous loading state queries, and disposal against the
    /// <see cref="BlobCache"/>.
    /// </summary>
    public interface IBlobPtr
    {
        bool IsNull { get; }

        bool CanGet(BlobCache blobCache);

        void WarmUp(BlobCache blobCache);
        BlobLoadingState GetLoadingState(BlobCache blobCache);

        void Dispose(BlobCache blobCache);
    }
}
