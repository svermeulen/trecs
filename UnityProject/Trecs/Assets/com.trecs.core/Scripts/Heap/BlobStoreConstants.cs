namespace Trecs
{
    /// <summary>
    /// Shared constants for blob store file naming and serialization.
    /// </summary>
    public static class BlobStoreConstants
    {
        // .bytes is Unity's convention for binary asset files.
        public const string FileExtension = ".bytes";

        public const string BlobDirName = "blobs";
        public const string ManifestFileName = "manifest" + FileExtension;

        public const long SerializationFlags = 0;
    }
}
