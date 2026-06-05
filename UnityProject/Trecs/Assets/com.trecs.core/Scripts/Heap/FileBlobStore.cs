using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Internal;

namespace Trecs
{
    /// <summary>
    /// A flat <c>BlobId → bytes</c> store on disk: one file per blob, named by the id. Pure bytes —
    /// the payload (a managed serialization or the native <c>NBLB</c> format) is the caller's
    /// concern, so the store is format- and version-agnostic. The canonical filesystem-backed
    /// <see cref="IOpaqueBlobStore"/> for persisting opaque (eager) snapshot/recording blobs; svkj's
    /// <c>DiskMemoize</c> also builds on it for its derivable output-cache.
    /// <para>
    /// GC is <b>recording-rooted</b>: <see cref="CollectGarbage"/> keeps a file iff its id is in the
    /// caller-supplied live set (the union of ids referenced by retained recordings / snapshots),
    /// deleting everything else. This is strictly safer than an LRU that could drop a still-needed
    /// blob.
    /// </para>
    /// </summary>
    public sealed class FileBlobStore : IOpaqueBlobStore
    {
        const string Extension = ".blob";
        const string TempExtension = ".blob.tmp";

        readonly string _directory;

        public FileBlobStore(string directory)
        {
            TrecsAssert.That(
                !string.IsNullOrEmpty(directory),
                "FileBlobStore directory must not be null or empty"
            );
            _directory = directory;
            Directory.CreateDirectory(_directory);
        }

        public string DirectoryPath => _directory;

        public bool Contains(BlobId id) => File.Exists(PathFor(id));

        /// <summary>
        /// Streams the bytes for <paramref name="id"/> straight to disk via
        /// <paramref name="writeContents"/>. The write is staged to a temp file and atomically moved
        /// into place, so a crash mid-write can't leave a truncated blob (and a concurrent reader
        /// never observes a half-written final file). Idempotent: an existing id is overwritten with
        /// identical content.
        /// </summary>
        public void Write(BlobId id, Action<Stream> writeContents)
        {
            TrecsAssert.That(writeContents != null, "writeContents must not be null");

            var finalPath = PathFor(id);
            var tempPath = finalPath + ".tmp";

            using (
                var stream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None
                )
            )
            {
                writeContents(stream);
            }

            // Swap the staged temp into place. File.Replace is an atomic same-volume rename when the
            // final already exists (no delete-then-move window where a reader sees no file); File.Move
            // covers the first-write case where there is nothing to replace.
            if (File.Exists(finalPath))
            {
                File.Replace(tempPath, finalPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, finalPath);
            }
        }

        public bool TryOpenRead(BlobId id, out Stream stream)
        {
            var path = PathFor(id);
            if (!File.Exists(path))
            {
                stream = null;
                return false;
            }
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan
            );
            return true;
        }

        /// <summary>Deletes the file for <paramref name="id"/>; returns true if one existed.</summary>
        public bool Delete(BlobId id)
        {
            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }

        /// <summary>Every blob id currently stored on disk.</summary>
        public IEnumerable<BlobId> EnumerateIds()
        {
            foreach (var path in Directory.GetFiles(_directory, "*" + Extension))
            {
                if (TryParseId(path, out var id))
                {
                    yield return id;
                }
            }
        }

        /// <summary>
        /// Deletes every stored blob whose id is <i>not</i> in <paramref name="liveIds"/> (plus any
        /// leftover temp files from interrupted writes). Returns the number of files deleted. Pass
        /// the union of ids referenced by all retained recordings / snapshots.
        /// </summary>
        public int CollectGarbage(IReadOnlyCollection<BlobId> liveIds)
        {
            TrecsAssert.That(liveIds != null, "liveIds must not be null");
            var live = liveIds as HashSet<BlobId> ?? new HashSet<BlobId>(liveIds);

            int deleted = 0;

            foreach (var path in Directory.GetFiles(_directory, "*" + Extension))
            {
                if (TryParseId(path, out var id) && !live.Contains(id))
                {
                    File.Delete(path);
                    deleted += 1;
                }
            }

            // Sweep stale temp files (an interrupted Write leaves a .blob.tmp behind).
            foreach (var path in Directory.GetFiles(_directory, "*" + TempExtension))
            {
                File.Delete(path);
                deleted += 1;
            }

            return deleted;
        }

        string PathFor(BlobId id) => Path.Combine(_directory, id.Value + Extension);

        static bool TryParseId(string path, out BlobId id)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (long.TryParse(name, out var value))
            {
                id = new BlobId(value);
                return true;
            }
            id = default;
            return false;
        }
    }
}
