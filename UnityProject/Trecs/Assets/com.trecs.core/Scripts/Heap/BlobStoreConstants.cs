namespace Trecs
{
    /// <summary>
    /// Shared constants for blob store file naming and serialization.
    /// </summary>
    public static class BlobStoreConstants
    {
        // Use .bytes since this can be directly used as assets inside the unity project
        // Unity seems to prefer this extension for binary files
        public const string FileExtension = ".bytes";

        public const string BlobDirName = "blobs";
        public const string ManifestFileName = "manifest" + FileExtension;

        public const long SerializationFlags = 0;
    }
}
